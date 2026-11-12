using System.Globalization;
using AutoBodyService.Application.Audit;
using AutoBodyService.Application.Prices;
using AutoBodyService.Domain.Prices;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Prices;

public sealed class PriceCatalog(ApplicationDbContext db, IAdminAuditWriter audit) : IPriceCatalog
{
    public async Task<IReadOnlyList<PublicPriceItem>> ListPublicAsync(CancellationToken cancellationToken)
    {
        var items = await db.PriceListItems.AsNoTracking().Where(x => x.IsPublished && !x.IsArchived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Name, x.Description, x.Price, x.IsFrom }).ToListAsync(cancellationToken);
        return items.Select(x => new PublicPriceItem(x.Id, x.Name, x.Description, x.Price,
            x.Price.ToString(CultureInfo.InvariantCulture), PriceListItem.Currency, x.IsFrom)).ToArray();
    }
    public async Task<IReadOnlyList<AdminPriceItem>> ListAdminAsync(CancellationToken cancellationToken) =>
        await db.PriceListItems.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new AdminPriceItem(x.Id, x.Name, x.Description, x.Price, PriceListItem.Currency, x.IsFrom,
                x.SortOrder, x.IsPublished, x.IsArchived, EF.Property<uint>(x, "Version"))).ToListAsync(cancellationToken);
    public Task<AdminPriceItem?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.PriceListItems.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new AdminPriceItem(x.Id, x.Name, x.Description, x.Price, PriceListItem.Currency, x.IsFrom,
                x.SortOrder, x.IsPublished, x.IsArchived, EF.Property<uint>(x, "Version"))).SingleOrDefaultAsync(cancellationToken);
    public async Task<AdminPriceItem> CreateAsync(string name, string? description, decimal price, bool isFrom, int sortOrder, bool isPublished, string actorId, CancellationToken cancellationToken)
    {
        var item = new PriceListItem(Guid.NewGuid(), name, description, price, isFrom, sortOrder, isPublished);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.PriceListItems.Add(item); audit.RecordPriceItemChange(actorId, item.Id, true);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Details(item);
    }
    public async Task<(PriceUpdateResult Result, AdminPriceItem? Item)> UpdateAsync(Guid id, uint version, PriceChange change, string actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.PriceListItems.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return (PriceUpdateResult.NotFound, null);
        if (db.Entry(item).Property<uint>("Version").CurrentValue != version) return (PriceUpdateResult.Conflict, null);
        var archived = change.IsArchived ?? item.IsArchived;
        var published = change.IsPublished ?? (archived ? false : item.IsPublished);
        try { item.Update(change.Name ?? item.Name, change.Description ?? item.Description, change.Price ?? item.Price,
            change.IsFrom ?? item.IsFrom, change.SortOrder ?? item.SortOrder, published, archived); }
        catch (ArgumentException) { return (PriceUpdateResult.Invalid, null); }
        if (!db.ChangeTracker.HasChanges()) return (PriceUpdateResult.Success, Details(item));
        audit.RecordPriceItemChange(actorId, item.Id, false);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (PriceUpdateResult.Success, Details(item));
        }
        catch (DbUpdateConcurrencyException) { return (PriceUpdateResult.Conflict, null); }
    }
    private AdminPriceItem Details(PriceListItem item) => new(item.Id, item.Name, item.Description, item.Price, PriceListItem.Currency,
        item.IsFrom, item.SortOrder, item.IsPublished, item.IsArchived, db.Entry(item).Property<uint>("Version").CurrentValue);
}
