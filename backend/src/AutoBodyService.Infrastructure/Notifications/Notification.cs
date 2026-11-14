namespace AutoBodyService.Infrastructure.Notifications;

// Restricted is a fail-closed value for legacy types that cannot be classified safely.
public enum NotificationAudience { Restricted = 0, Personal = 1, Administrator = 2, SuperAdministrator = 3 }

public sealed class Notification
{
    public Guid Id { get; set; }
    public string RecipientId { get; set; } = null!;
    public Guid? AppointmentId { get; set; }
    public string Type { get; set; } = null!;
    public required NotificationAudience Audience { get; set; }
    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ReadAtUtc { get; set; }
    public string EventKey { get; set; } = null!;
}

public sealed class ScheduledReminder
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    // Null is the administrator audience, resolved against current roles at execution time.
    public string? RecipientId { get; set; }
    public string Type { get; set; } = null!;
    public string? Outcome { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string EventKey { get; set; } = null!;
}
