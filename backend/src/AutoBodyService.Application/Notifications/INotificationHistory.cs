namespace AutoBodyService.Application.Notifications;

/// <summary>A recipient's notification with an optional authorized appointment link.</summary>
public sealed record NotificationItem(Guid Id, string Type, string Title, string Message,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? ReadAtUtc, string? Link);

public interface INotificationHistory
{
    Task<IReadOnlyList<NotificationItem>> GetAsync(string recipientId, bool administrator, bool? unread, CancellationToken cancellationToken);
    Task<bool> MarkReadAsync(string recipientId, Guid id, CancellationToken cancellationToken);
    Task MarkAllReadAsync(string recipientId, CancellationToken cancellationToken);
}
