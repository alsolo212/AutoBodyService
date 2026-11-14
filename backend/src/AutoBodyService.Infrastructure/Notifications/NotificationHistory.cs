using AutoBodyService.Application.Notifications;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Notifications;

public sealed class NotificationHistory(ApplicationDbContext db, TimeProvider clock) : INotificationHistory
{
    // Evaluate role membership in the same database statement as each read/update,
    // so a role snapshot from the cookie cannot authorize a later mutation.
    private IQueryable<Notification> VisibleTo(string recipientId) => db.Notifications.Where(n =>
        n.RecipientId == recipientId &&
        (n.Audience == NotificationAudience.Personal ||
         (n.Audience == NotificationAudience.Administrator && db.UserRoles.Any(m => m.UserId == recipientId &&
             db.Roles.Any(r => r.Id == m.RoleId && (r.Name == ApplicationRoles.Admin || r.Name == ApplicationRoles.SuperAdmin)))) ||
         (n.Audience == NotificationAudience.SuperAdministrator && db.UserRoles.Any(m => m.UserId == recipientId &&
             db.Roles.Any(r => r.Id == m.RoleId && r.Name == ApplicationRoles.SuperAdmin)))));

    public async Task<IReadOnlyList<NotificationItem>> GetAsync(string recipientId, bool? unread, CancellationToken cancellationToken)
    {
        var query = VisibleTo(recipientId).AsNoTracking();
        if (unread.HasValue) query = query.Where(n => unread.Value ? n.ReadAtUtc == null : n.ReadAtUtc != null);
        var items = await (from n in query
            join a in db.Appointments.AsNoTracking() on n.AppointmentId equals (Guid?)a.Id into appointments
            from a in appointments.DefaultIfEmpty()
            orderby n.CreatedAtUtc descending, n.Id descending
            select new { n.Id, n.Type, n.Audience, n.Title, n.Message, n.CreatedAtUtc, n.ReadAtUtc, n.AppointmentId, OwnerId = a == null ? null : a.UserId })
            .ToListAsync(cancellationToken);
        return items.Select(n => new NotificationItem(n.Id, n.Type, n.Title, n.Message, n.CreatedAtUtc, n.ReadAtUtc,
            n.AppointmentId is null ? null : n.Audience != NotificationAudience.Personal
                ? $"/admin/appointments/{n.AppointmentId}" : n.OwnerId == recipientId ? $"/account/appointments/{n.AppointmentId}" : null)).ToArray();
    }

    public async Task<bool> MarkReadAsync(string recipientId, Guid id, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var visible = VisibleTo(recipientId).Where(n => n.Id == id);
        var count = await visible.Where(n => n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), cancellationToken);
        return count != 0 || await visible.AnyAsync(cancellationToken);
    }

    public async Task MarkAllReadAsync(string recipientId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await VisibleTo(recipientId).Where(n => n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), cancellationToken);
    }
}
