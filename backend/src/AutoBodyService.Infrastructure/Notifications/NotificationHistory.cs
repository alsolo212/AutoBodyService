using AutoBodyService.Application.Notifications;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Notifications;

public sealed class NotificationHistory(ApplicationDbContext db, TimeProvider clock) : INotificationHistory
{
    public async Task<IReadOnlyList<NotificationItem>> GetAsync(string recipientId, bool administrator, bool? unread, CancellationToken cancellationToken)
    {
        var query = db.Notifications.AsNoTracking().Where(n => n.RecipientId == recipientId);
        if (unread.HasValue) query = query.Where(n => unread.Value ? n.ReadAtUtc == null : n.ReadAtUtc != null);
        var items = await (from n in query
            join a in db.Appointments.AsNoTracking() on n.AppointmentId equals (Guid?)a.Id into appointments
            from a in appointments.DefaultIfEmpty()
            orderby n.CreatedAtUtc descending, n.Id descending
            select new { n.Id, n.Type, n.Title, n.Message, n.CreatedAtUtc, n.ReadAtUtc, n.AppointmentId, OwnerId = a == null ? null : a.UserId })
            .ToListAsync(cancellationToken);
        return items.Select(n => new NotificationItem(n.Id, n.Type, n.Title, n.Message, n.CreatedAtUtc, n.ReadAtUtc,
            n.AppointmentId is null ? null : n.Type.StartsWith("Admin", StringComparison.Ordinal) && administrator
                ? $"/admin/appointments/{n.AppointmentId}" : n.OwnerId == recipientId ? $"/account/appointments/{n.AppointmentId}" : null)).ToArray();
    }

    public async Task<bool> MarkReadAsync(string recipientId, Guid id, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var count = await db.Notifications.Where(n => n.RecipientId == recipientId && n.Id == id && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), cancellationToken);
        return count != 0 || await db.Notifications.AnyAsync(n => n.RecipientId == recipientId && n.Id == id, cancellationToken);
    }

    public async Task MarkAllReadAsync(string recipientId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await db.Notifications.Where(n => n.RecipientId == recipientId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), cancellationToken);
    }
}
