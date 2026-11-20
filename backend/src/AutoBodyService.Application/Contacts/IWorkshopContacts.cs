namespace AutoBodyService.Application.Contacts;

public sealed record WorkshopContacts(string? Phone, string? Address, int Version);

public interface IWorkshopContacts
{
    Task<WorkshopContacts> GetAsync(CancellationToken cancellationToken);
    Task<WorkshopContacts?> UpdateAsync(string phone, string address, int version, string actorId, CancellationToken cancellationToken);
}
