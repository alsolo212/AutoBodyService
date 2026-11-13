namespace AutoBodyService.Application.Notifications;

/// <summary>Signals recipients after notification records have committed.</summary>
public interface INotificationRealtimePublisher
{
    Task PublishAsync(IReadOnlyCollection<string> recipients);
}
