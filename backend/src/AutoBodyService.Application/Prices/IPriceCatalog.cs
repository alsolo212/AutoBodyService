namespace AutoBodyService.Application.Prices;

/// <summary>Published indicative price; PriceExact preserves its decimal representation for browser display.</summary>
public sealed record PublicPriceItem(Guid Id, string Name, string Description, decimal Price, string PriceExact, string Currency, bool IsFrom);
/// <summary>Administrative price record, including its optimistic concurrency version.</summary>
public sealed record AdminPriceItem(Guid Id, string Name, string Description, decimal Price, string Currency, bool IsFrom,
    int SortOrder, bool IsPublished, bool IsArchived, uint Version);
public sealed record PriceChange(string? Name, string? Description, decimal? Price, bool? IsFrom, int? SortOrder, bool? IsPublished, bool? IsArchived);
public enum PriceUpdateResult { Success, NotFound, Conflict, Invalid }
public interface IPriceCatalog
{
    Task<IReadOnlyList<PublicPriceItem>> ListPublicAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminPriceItem>> ListAdminAsync(CancellationToken cancellationToken);
    Task<AdminPriceItem?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminPriceItem> CreateAsync(string name, string? description, decimal price, bool isFrom, int sortOrder, bool isPublished, string actorId, CancellationToken cancellationToken);
    Task<(PriceUpdateResult Result, AdminPriceItem? Item)> UpdateAsync(Guid id, uint version, PriceChange change, string actorId, CancellationToken cancellationToken);
}
