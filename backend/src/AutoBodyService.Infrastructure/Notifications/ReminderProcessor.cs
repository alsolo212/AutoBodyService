using AutoBodyService.Application.Notifications;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Notifications;

/// <summary>Processes persisted inspection reminders under the same appointment lock as cancellations.</summary>
public sealed class ReminderProcessor(ApplicationDbContext db, TimeProvider clock, INotificationRealtimePublisher? publisher = null)
{
    public static readonly TimeSpan MaximumLateness = TimeSpan.FromSeconds(30);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public async Task<int> ProcessDueAsync(DateTimeOffset workerStartedAtUtc, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await db.ScheduledReminders.AsNoTracking().Where(r => r.ProcessedAtUtc == null && r.DueAtUtc <= now)
            .OrderBy(r => r.DueAtUtc).ThenBy(r => r.Id).Select(r => new { r.Id, r.AppointmentId }).Take(100).ToListAsync(cancellationToken);
        foreach (var candidate in due)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Same lock order as customer/operator cancellation, so an accepted cancellation
            // and a reminder cannot commit contradictory results concurrently.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {candidate.AppointmentId} FOR UPDATE", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"ScheduledReminders\" WHERE \"Id\" = {candidate.Id} FOR UPDATE", cancellationToken);
            var reminder = await db.ScheduledReminders.SingleAsync(r => r.Id == candidate.Id, cancellationToken);
            if (reminder.ProcessedAtUtc is not null) continue;
            var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == reminder.AppointmentId, cancellationToken);
            now = clock.GetUtcNow();
            // A corrected system clock must not make a previously selected job fire early.
            if (reminder.DueAtUtc > now) continue;
            var lead = reminder.Type switch
            {
                "Client24Hours" => TimeSpan.FromHours(24),
                "Client2Hours" => TimeSpan.FromHours(2),
                "Admin30Minutes" => TimeSpan.FromMinutes(30),
                _ => (TimeSpan?)null
            };
            string? skip = appointment.Status == AppointmentStatus.Cancelled ? "SkippedCancelled" :
                appointment.Status != AppointmentStatus.AwaitingInspection ? "SkippedState" :
                appointment.InspectionStartUtc <= now ? "SkippedPast" :
                lead is null || appointment.InspectionStartUtc - lead.Value != reminder.DueAtUtc ? "SkippedChanged" :
                reminder.DueAtUtc < workerStartedAtUtc || now - reminder.DueAtUtc > MaximumLateness ? "SkippedLate" : null;
            if (skip is null)
            {
                var recipients = reminder.Type == "Admin30Minutes"
                    ? await NotificationEvents.AdministratorsAsync(db, cancellationToken)
                    : await EligibleOwnerAsync(reminder, appointment.UserId, cancellationToken);
                var local = TimeZoneInfo.ConvertTime(appointment.InspectionStartUtc, Zone);
                var (type, title, text) = reminder.Type switch
                {
                    "Client24Hours" => ("InspectionReminder24Hours", "Нагадування про огляд", "До огляду залишилася доба."),
                    "Client2Hours" => ("InspectionReminder2Hours", "Нагадування про огляд", "До огляду залишилося дві години."),
                    _ => ("AdminInspectionReminder30Minutes", "Наближається огляд", "Огляд відбудеться за 30 хвилин.")
                };
                foreach (var recipient in recipients)
                    NotificationEvents.Add(db, recipient, appointment.Id, reminder.Type == "Admin30Minutes" ? NotificationAudience.Administrator : NotificationAudience.Personal, type, title,
                        $"{text} Час огляду: {local:dd.MM.yyyy HH:mm} (Київ, UTC{local:zzz}).", now,
                        $"reminder:{reminder.Id}:{recipient}");
                skip = recipients.Count == 0 ? "SkippedRecipient" : "Sent";
            }
            reminder.ProcessedAtUtc = now;
            reminder.Outcome = skip;
            var changed = NotificationDispatch.PendingRecipients(db);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await NotificationDispatch.AfterCommitAsync(publisher, changed);
        }
        return due.Count;
    }

    private async Task<List<string>> EligibleOwnerAsync(ScheduledReminder reminder, string ownerId, CancellationToken cancellationToken)
    {
        if (reminder.RecipientId != ownerId) return [];
        return await (from user in db.Users.AsNoTracking()
                      join membership in db.Set<IdentityUserRole<string>>() on user.Id equals membership.UserId
                      join role in db.Roles.AsNoTracking() on membership.RoleId equals role.Id
                      where user.Id == ownerId && !user.IsBlocked &&
                          (role.Name == ApplicationRoles.User || role.Name == ApplicationRoles.Admin || role.Name == ApplicationRoles.SuperAdmin)
                      select user.Id).Distinct().ToListAsync(cancellationToken);
    }
}
