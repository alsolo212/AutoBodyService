namespace AutoBodyService.Application.Audit;

/// <summary>Filters for an administrative history page. The upper UTC bound is exclusive.</summary>
public sealed record AuditQuery(DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? Action, string? ActorId,
    bool Descending, int Page, int PageSize);

/// <summary>An immutable administrative event with a restricted set of safe details.</summary>
public sealed record AuditEntry(Guid Id, DateTimeOffset OccurredAtUtc, string ActorId, string Action,
    string ObjectType, string ObjectId, IReadOnlyDictionary<string, object?> Details, string? ActorName);

/// <summary>A server-paginated selection of administrative events.</summary>
public sealed record AuditPage(int Total, int Page, int PageSize, IReadOnlyList<AuditEntry> Items);
