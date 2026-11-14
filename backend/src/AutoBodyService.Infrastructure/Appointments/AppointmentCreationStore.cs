using AutoBodyService.Application.Appointments;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Application.Notifications;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class AppointmentCreationStore(ApplicationDbContext db, IInspectionSlotsService slots, TimeProvider clock, INotificationRealtimePublisher? publisher = null) : IAppointmentCreationStore
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public async Task<CreateAppointmentResult> SaveIfAvailableAsync(Appointment appointment, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // All booking writers serialize on the same row that schedule updates modify.
        await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"InspectionSchedules\" WHERE \"Id\" = 1 FOR UPDATE", cancellationToken);
        if (!await db.Users.AnyAsync(u => u.Id == appointment.UserId && !u.IsBlocked && !u.MustChangePassword, cancellationToken))
            return new(CreateAppointmentStatus.Unauthorized);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(appointment.InspectionStartUtc, Zone).DateTime);
        if (localDate.Year is < 2 or > 9998 || !(await slots.GetAvailableAsync(localDate, cancellationToken)).Any(s => s.StartUtc == appointment.InspectionStartUtc))
            return new(CreateAppointmentStatus.Unavailable);
        var now = clock.GetUtcNow();
        if (appointment.InspectionStartUtc <= now) return new(CreateAppointmentStatus.Unavailable);
        db.Appointments.Add(appointment);
        var admins = await (from user in db.Users.AsNoTracking()
                            join userRole in db.Set<IdentityUserRole<string>>() on user.Id equals userRole.UserId
                            join role in db.Roles on userRole.RoleId equals role.Id
                            where !user.IsBlocked && (role.Name == ApplicationRoles.Admin || role.Name == ApplicationRoles.SuperAdmin)
                            select user.Id).Distinct().ToListAsync(cancellationToken);
        var local = TimeZoneInfo.ConvertTime(appointment.InspectionStartUtc, Zone);
        AddNotification(appointment.UserId, NotificationAudience.Personal, "AppointmentCreated", "Запис на огляд створено", $"Огляд заплановано на {local:dd.MM.yyyy HH:mm} (Київ, UTC{local:zzz}).");
        foreach (var admin in admins) AddNotification(admin, NotificationAudience.Administrator, "AdminAppointmentCreated", "Новий запис на огляд", "Створено новий запис на огляд. Перегляньте замовлення та зв’яжіться з клієнтом за потреби.");
        foreach (var (type, lead) in new[] { ("Client24Hours", TimeSpan.FromHours(24)), ("Client2Hours", TimeSpan.FromHours(2)), ("Admin30Minutes", TimeSpan.FromMinutes(30)) })
        {
            var due = appointment.InspectionStartUtc - lead;
            if (due > now) db.ScheduledReminders.Add(new ScheduledReminder { Id = Guid.NewGuid(), AppointmentId = appointment.Id, RecipientId = type == "Admin30Minutes" ? null : appointment.UserId, Type = type, DueAtUtc = due, EventKey = $"appointment:{appointment.Id}:reminder:{type}" });
        }
        var recipients = NotificationDispatch.PendingRecipients(db);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Appointments_InspectionStartUtc" })
        { return new(CreateAppointmentStatus.Unavailable); }
        await NotificationDispatch.AfterCommitAsync(publisher, recipients);
        return new(CreateAppointmentStatus.Created, appointment.Id, appointment.InspectionStartUtc);

        void AddNotification(string recipient, NotificationAudience audience, string type, string title, string message) => db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(), RecipientId = recipient, AppointmentId = appointment.Id, Audience = audience, Type = type, Title = title,
            Message = message, CreatedAtUtc = now, EventKey = $"appointment:{appointment.Id}:{type}:{recipient}"
        });
    }
}
