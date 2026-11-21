using AutoBodyService.Application.Appointments;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Application.Audit;
using AutoBodyService.Infrastructure.Persistence;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Application.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class AdminAppointmentStore(ApplicationDbContext db, IAdminAuditWriter audit, TimeProvider clock, INotificationRealtimePublisher? publisher = null, IRepairPlanning? planning = null) : IAdminAppointmentStore
{
    public async Task<AdminAppointmentResult> ExecuteAsync(Guid id, uint version, string actorId,
        Func<Domain.Appointments.Appointment, DateTimeOffset, AdminAppointmentMutation> mutate, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize capacity changes with all administrative status and plan writes.
        await RepairCapacityStore.LockAsync(db, cancellationToken);
        // Customer cancellation uses the same row lock; xmin also rejects other stale writers.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {id} FOR UPDATE", cancellationToken);
        var appointment = await db.Appointments.Include(a => a.RepairPlan).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (appointment is null) return AdminAppointmentResult.NotFound;
        if (db.Entry(appointment).Property<uint>("Version").CurrentValue != version) return AdminAppointmentResult.Conflict;
        var oldStatus = appointment.Status;
        var oldStart = appointment.RepairPlan?.PlannedStartUtc;
        var activePause = oldStatus == AppointmentStatus.Paused
            ? await db.RepairPauses.SingleOrDefaultAsync(p => p.AppointmentId == id && p.EndedAtUtc == null, cancellationToken) : null;
        if (oldStatus == AppointmentStatus.Paused && activePause is null) return AdminAppointmentResult.Conflict;
        var mutation = mutate(appointment, clock.GetUtcNow());
        if (mutation.Result != AdminAppointmentResult.Success) return mutation.Result;
        if (mutation.DelayToken is { } delayToken)
        {
            var finish = RepairCapacityStore.LocalDate(appointment.RepairPlan!.EstimatedFinishUtc);
            if (planning is null || !await planning.VerifyDelayAsync(id, finish, delayToken, cancellationToken)) return AdminAppointmentResult.Conflict;
        }
        if (mutation.OfferPauseId is { } offerPause)
        {
            var offer = planning is null ? null : await planning.PauseOfferAsync(offerPause, cancellationToken);
            if (offer?.Candidate?.Id != id || offer.Candidate.Version != version) return AdminAppointmentResult.Conflict;
        }
        if (mutation.EarlyOffer)
        {
            var offer = planning is null ? null : (await planning.OverviewAsync(cancellationToken)).EarlyOpportunity;
            if (offer?.Id != id || offer.Version != version) return AdminAppointmentResult.Conflict;
        }
        if (mutation.DatesChanged && appointment.RepairPlan is { } revised && oldStart != revised.PlannedStartUtc &&
            RepairCapacityStore.LocalDate(revised.PlannedStartUtc) > RepairCapacityStore.LocalDate(clock.GetUtcNow()).AddYears(1))
            return AdminAppointmentResult.Invalid;
        if (appointment.Status == AppointmentStatus.InProgress && oldStatus != AppointmentStatus.InProgress)
        {
            var places = (await db.RepairCapacity.SingleAsync(cancellationToken)).Workplaces;
            var actual = await RepairCapacityStore.ActualCountAsync(db, cancellationToken);
            if (oldStatus == AppointmentStatus.Paused && activePause is { WorkplaceReleased: false }) actual--;
            if (places is null || actual >= places.Value) return AdminAppointmentResult.Conflict;
        }
        if (appointment.Status == AppointmentStatus.Paused && mutation.DatesChanged && mutation.DelayToken is null)
            return AdminAppointmentResult.Conflict;
        if (appointment.Status is AppointmentStatus.Scheduled or AppointmentStatus.InProgress &&
            (oldStatus == AppointmentStatus.AwaitingInspection || mutation.DatesChanged) && mutation.DelayToken is null)
        {
            var plan = appointment.RepairPlan!;
            var start = RepairCapacityStore.LocalDate(plan.PlannedStartUtc);
            var end = RepairCapacityStore.LocalDate(plan.EstimatedFinishUtc);
            var today = RepairCapacityStore.LocalDate(clock.GetUtcNow());
            if (oldStart != plan.PlannedStartUtc && (start < today || start > today.AddYears(1))) return AdminAppointmentResult.Invalid;
            var places = (await db.RepairCapacity.SingleAsync(cancellationToken)).Workplaces;
            var reservations = await RepairCapacityStore.ReservationsAsync(db, id, today, cancellationToken);
            if (places is null || !RepairAvailability.Fits(new(start, end), reservations, places.Value)) return AdminAppointmentResult.Conflict;
        }
        if (mutation.StatusEvent is null && mutation.Confirmation is null && !mutation.PriceChanged && !mutation.DatesChanged)
            return AdminAppointmentResult.Success;
        if (mutation.StatusEvent is { } statusEvent)
        {
            if (mutation.Pause is { } pause) db.RepairPauses.Add(pause);
            if (statusEvent.FromStatus == AppointmentStatus.Paused && activePause is not null) activePause.End(actorId, statusEvent.ChangedAtUtc);
            if (statusEvent.FromStatus == AppointmentStatus.InProgress && statusEvent.ToStatus != AppointmentStatus.InProgress)
            {
                var activeAssignments = await db.EmployeeAssignments.Where(a => a.AppointmentId == id && a.ReleasedAtUtc == null).ToListAsync(cancellationToken);
                foreach (var assignment in activeAssignments)
                {
                    assignment.Release(actorId, statusEvent.ChangedAtUtc);
                    audit.RecordAssignmentChange(actorId, assignment.Id, id, assignment.EmployeeId, true, true);
                }
            }
            db.AppointmentStatusEvents.Add(statusEvent);
            if (statusEvent.ToStatus == AppointmentStatus.Cancelled)
                await db.ScheduledReminders.Where(r => r.AppointmentId == id && r.ProcessedAtUtc == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.ProcessedAtUtc, statusEvent.ChangedAtUtc).SetProperty(r => r.Outcome, "SkippedCancelled"), cancellationToken);
            var (title, message) = statusEvent.ToStatus switch
            {
                AppointmentStatus.Scheduled => ("Ремонт заплановано", "Умови ремонту погоджено. Перегляньте план замовлення."),
                AppointmentStatus.InProgress => ("Ремонт розпочато", "Ваш автомобіль передано в роботу."),
                AppointmentStatus.Paused => ("Ремонт призупинено", "Ремонт тимчасово призупинено. Зв’яжіться з майстернею для уточнень."),
                AppointmentStatus.AwaitingPayment => ("Автомобіль готовий", "Ремонт завершено. Зв’яжіться з нами щодо оплати та отримання автомобіля."),
                AppointmentStatus.Completed => ("Замовлення завершено", "Оплату та видачу автомобіля підтверджено."),
                AppointmentStatus.Cancelled => ("Запис скасовано", "Ваш запис скасовано оператором."),
                _ => throw new InvalidOperationException("Unexpected transition.")
            };
            var eventId = $"status:{statusEvent.Id}";
            if (appointment.UserId is { } owner) NotificationEvents.Add(db, owner, id, NotificationAudience.Personal, $"Appointment{statusEvent.ToStatus}", title, message,
                statusEvent.ChangedAtUtc, $"{eventId}:customer:{appointment.UserId}");
            if (statusEvent.ToStatus == AppointmentStatus.AwaitingPayment)
                await NotificationEvents.AddForAdministratorsAsync(db, id, "AdminReadyForPayment", "Автомобіль готовий до видачі",
                    "Ремонт завершено. Потрібно зв’язатися з клієнтом щодо оплати та видачі.", statusEvent.ChangedAtUtc, eventId, cancellationToken);
            if (statusEvent.ToStatus == AppointmentStatus.Scheduled)
                await NotificationEvents.AddForAdministratorsAsync(db, id, "AdminContactAgreement", "Завдання зв’язатися з клієнтом",
                    "Умови ремонту погоджено. За потреби зв’яжіться з клієнтом.", statusEvent.ChangedAtUtc, eventId, cancellationToken);
            audit.RecordAppointmentStatusChange(actorId, id, statusEvent.FromStatus!.Value, statusEvent.ToStatus, statusEvent.Id);
        }
        if (mutation.Confirmation is not null) db.PaymentHandoverConfirmations.Add(mutation.Confirmation);
        if (mutation.PriceChanged || mutation.DatesChanged)
        {
            audit.RecordRepairPlanChange(actorId, id, mutation.PriceChanged, mutation.DatesChanged);
            var now = clock.GetUtcNow();
            var eventId = $"plan:{id}:{Guid.NewGuid()}";
            if (appointment.UserId is { } owner) NotificationEvents.Add(db, owner, id, NotificationAudience.Personal, "RepairPlanChanged", "План ремонту змінено",
                "Вартість або терміни ремонту змінено. Перегляньте актуальний план замовлення.", now, $"{eventId}:customer:{appointment.UserId}");
            if (appointment.Status == AppointmentStatus.Scheduled && oldStatus != AppointmentStatus.AwaitingInspection)
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
