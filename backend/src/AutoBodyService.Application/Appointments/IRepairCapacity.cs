namespace AutoBodyService.Application.Appointments;

public sealed record RepairCapacityDay(DateOnly Date, int Reserved);
public sealed record RepairCapacityOverview(int? Workplaces, int InProgress, IReadOnlyList<RepairCapacityDay> Days);
public sealed record RepairAvailabilityPreview(DateOnly Today, DateOnly LatestStart, DateOnly? NearestStart, DateOnly? Finish, bool Fits, int? Workplaces, int InProgress);

public interface IRepairCapacity
{
    Task<RepairCapacityOverview> OverviewAsync(DateOnly firstDay, CancellationToken cancellationToken);
    Task<bool> SetWorkplacesAsync(int workplaces, string actorId, CancellationToken cancellationToken);
    Task<RepairAvailabilityPreview?> PreviewAsync(DateOnly start, int durationDays, CancellationToken cancellationToken);
}
