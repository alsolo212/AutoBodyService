using AutoBodyService.Application.Audit;
using AutoBodyService.Application.Contacts;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Contacts;

public sealed class WorkshopContactsRecord
{
    public int Id { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public int Version { get; set; }
}

public sealed class WorkshopContactsStore(ApplicationDbContext db, IAdminAuditWriter audit) : IWorkshopContacts
{
    public async Task<WorkshopContacts> GetAsync(CancellationToken cancellationToken)
    {
        var item = await db.WorkshopContacts.AsNoTracking().SingleAsync(cancellationToken);
        return new(item.Phone, item.Address, item.Version);
    }

    public async Task<WorkshopContacts?> UpdateAsync(string phone, string address, int version, string actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.WorkshopContacts.SingleAsync(cancellationToken);
        if (item.Version != version) return null;
        var phoneChanged = item.Phone != phone;
        var addressChanged = item.Address != address;
        if (phoneChanged || addressChanged)
        {
            item.Phone = phone;
            item.Address = address;
            item.Version = checked(item.Version + 1);
            if (phoneChanged) audit.RecordWorkshopContactChange(actorId, true);
            if (addressChanged) audit.RecordWorkshopContactChange(actorId, false);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { return null; }
        }
        await transaction.CommitAsync(cancellationToken);
        return new(item.Phone, item.Address, item.Version);
    }
}
