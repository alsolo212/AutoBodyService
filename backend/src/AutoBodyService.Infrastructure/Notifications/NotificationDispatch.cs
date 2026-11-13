using AutoBodyService.Application.Notifications;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Notifications;

internal static class NotificationDispatch
{
    internal static string[] PendingRecipients(ApplicationDbContext db) => db.ChangeTracker.Entries<Notification>()
        .Where(e => e.State == EntityState.Added).Select(e => e.Entity.RecipientId).Distinct().ToArray();

    // Delivery is best effort. A disconnected client recovers from the committed REST history.
    internal static async Task AfterCommitAsync(INotificationRealtimePublisher? publisher, string[] recipients)
    {
        if (publisher is null || recipients.Length == 0) return;
        try { await publisher.PublishAsync(recipients); }
        catch { /* Failure cannot roll back a committed business transaction. */ }
    }
}
