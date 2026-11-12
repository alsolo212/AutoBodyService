namespace AutoBodyService.Infrastructure.Audit;

/// <summary>An append-only administrative action; details are produced by the audit writer.</summary>
public sealed class AdminAuditEvent
{
    private AdminAuditEvent() { ActorId = Action = ObjectType = ObjectId = Details = null!; }

    internal AdminAuditEvent(string actorId, string action, string objectType, string objectId, DateTimeOffset occurredAtUtc, string details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (occurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Time must use UTC.", nameof(occurredAtUtc));
        Id = Guid.NewGuid();
        ActorId = actorId;
        Action = action;
        ObjectType = objectType;
        ObjectId = objectId;
        OccurredAtUtc = occurredAtUtc;
        Details = details;
    }

    public Guid Id { get; private set; }
    public string ActorId { get; private set; }
    public string Action { get; private set; }
    public string ObjectType { get; private set; }
    public string ObjectId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public string Details { get; private set; }
}
