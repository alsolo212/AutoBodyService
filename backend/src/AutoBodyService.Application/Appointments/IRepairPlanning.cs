namespace AutoBodyService.Application.Appointments;

public sealed record RepairPlanningOrder(Guid Id, string CustomerName, string CustomerPhone, string Make, string Model,
    DateOnly Start, DateOnly Finish, DateOnly? AlternativeStart, uint Version);
public sealed record RepairPauseOffer(Guid PauseId, DateOnly ExpectedReturn, RepairPlanningOrder? Candidate);
public sealed record RepairDelayPreview(Guid AppointmentId, DateOnly Finish, string ConfirmationToken, IReadOnlyList<RepairPlanningOrder> Conflicts);
public sealed record RepairPlanningOverview(IReadOnlyList<RepairPlanningOrder> Conflicts, RepairPlanningOrder? EarlyOpportunity);

public interface IRepairPlanning
{
    Task<RepairPlanningOverview> OverviewAsync(CancellationToken cancellationToken);
    Task<RepairPauseOffer?> PauseOfferAsync(Guid pauseId, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> DeclinePauseOfferAsync(Guid pauseId, Guid candidateId, string actorId, CancellationToken cancellationToken);
    Task<RepairDelayPreview?> PreviewDelayAsync(Guid id, DateOnly finish, CancellationToken cancellationToken);
    Task<bool> VerifyDelayAsync(Guid id, DateOnly finish, string token, CancellationToken cancellationToken);
}
