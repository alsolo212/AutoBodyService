namespace AutoBodyService.Application.InspectionSchedule;

/// <summary>A free inspection window identified by its UTC instants.</summary>
public sealed record InspectionSlot(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>Reads current availability. Booking still requires transactional revalidation and a database constraint.</summary>
public interface IInspectionSlotsService
{
    Task<IReadOnlyList<InspectionSlot>> GetAvailableAsync(DateOnly date, CancellationToken cancellationToken);
}
