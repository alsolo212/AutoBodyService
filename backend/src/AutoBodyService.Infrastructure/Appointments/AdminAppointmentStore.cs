using AutoBodyService.Application.Appointments;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Application.Audit;
using AutoBodyService.Infrastructure.Persistence;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Application.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class AdminAppointmentStore(ApplicationDbContext db, IAdminAuditWriter audit, TimeProvider clock, INotificationRealtimePublisher? publisher = null) : IAdminAppointmentStore
{
    public async Task<AdminAppointmentResult> ExecuteAsync(Guid id, uint version, string actorId,
        Func<Domain.Appointments.Appointment, DateTimeOffset, AdminAppointmentMutation> mutate, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Customer cancellation uses the same row lock; xmin also rejects other stale writers.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {id} FOR UPDATE", cancellationToken);
        var appointment = await db.Appointments.Include(a => a.RepairPlan).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (appointment is null) return AdminAppointmentResult.NotFound;
        if (db.Entry(appointment).Property<uint>("Version").CurrentValue != version) return AdminAppointmentResult.Conflict;
        var mutation = mutate(appointment, clock.GetUtcNow());
        if (mutation.Result != AdminAppointmentResult.Success) return mutation.Result;
        if (mutation.StatusEvent is null && mutation.Confirmation is null && !mutation.PriceChanged && !mutation.DatesChanged)
            return AdminAppointmentResult.Success;
        if (mutation.StatusEvent is { } statusEvent)
        {
            if (statusEvent.FromStatus == AppointmentStatus.InProgress && statusEvent.ToStatus != AppointmentStatus.InProgress)
            {
                var activeAssignments = await db.EmployeeAssignments.Where(a => a.AppointmentId == id && a.ReleasedAtUtc == null).ToListAsync(cancellationToken);
                foreach (var assignment in activeAssignments) assignment.Release(actorId, statusEvent.ChangedAtUtc);
            }
            db.AppointmentStatusEvents.Add(statusEvent);
            var (title, message) = statusEvent.ToStatus switch
            {
                AppointmentStatus.Scheduled => ("Ремонт заплановано", "Умови ремонту погоджено. Перегляньте план замовлення."),
                AppointmentStatus.InProgress => ("Ремонт розпочато", "Ваш автомобіль передано в роботу."),
                AppointmentStatus.AwaitingPayment => ("Автомобіль готовий", "Ремонт завершено. Зв’яжіться з нами щодо оплати та отримання автомобіля."),
                AppointmentStatus.Completed => ("Замовлення завершено", "Оплату та видачу автомобіля підтверджено."),
                AppointmentStatus.Cancelled => ("Запис скасовано", "Ваш запис скасовано оператором."),
                _ => throw new InvalidOperationException("Unexpected transition.")
            };
            var eventId = $"status:{statusEvent.Id}";
            NotificationEvents.Add(db, appointment.UserId, id, $"Appointment{statusEvent.ToStatus}", title, message,
                statusEvent.ChangedAtUtc, $"{eventId}:customer:{appointment.UserId}");
            if (statusEvent.ToStatus == AppointmentStatus.AwaitingPayment)
                await NotificationEvents.AddForAdministratorsAsync(db, id, "AdminReadyForPayment", "Автомобіль готовий до видачі",
                    "Ремонт завершено. Потрібно зв’язатися з клієнтом щодо оплати та видачі.", statusEvent.ChangedAtUtc, eventId, cancellationToken);
            if (statusEvent.ToStatus == AppointmentStatus.Scheduled)
                await NotificationEvents.AddForAdministratorsAsync(db, id, "AdminContactAgreement", "Завдання зв’язатися з клієнтом",
                    "Умови ремонту погоджено. За потреби зв’яжіться з клієнтом.", statusEvent.ChangedAtUtc, eventId, cancellationToken);
            audit.RecordAppointmentStatusChange(actorId, id, statusEvent.FromStatus!.Value, statusEvent.ToStatus);
        }
        if (mutation.Confirmation is not null) db.PaymentHandoverConfirmations.Add(mutation.Confirmation);
        if (mutation.PriceChanged || mutation.DatesChanged)
        {
            audit.RecordRepairPlanChange(actorId, id, mutation.PriceChanged, mutation.DatesChanged);
            var now = clock.GetUtcNow();
            var eventId = $"plan:{id}:{Guid.NewGuid()}";
            NotificationEvents.Add(db, appointment.UserId, id, "RepairPlanChanged", "План ремонту змінено",
                "Вартість або терміни ремонту змінено. Перегляньте актуальний план замовлення.", now, $"{eventId}:customer:{appointment.UserId}");
            if (appointment.Status == AppointmentStatus.Scheduled)
                await NotificationEvents.AddForAdministratorsAsync(db, id, "AdminContactPlanChanged", "Завдання зв’язатися з клієнтом",
                    "План ремонту змінено. За потреби зв’яжіться з клієнтом.", now, eventId, cancellationToken);
        }
        var recipients = NotificationDispatch.PendingRecipients(db);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await NotificationDispatch.AfterCommitAsync(publisher, recipients);
            return AdminAppointmentResult.Success;
        }
        catch (DbUpdateConcurrencyException) { return AdminAppointmentResult.Conflict; }
    }
}
