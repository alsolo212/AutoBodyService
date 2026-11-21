using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Appointments;

public enum AdminAppointmentResult { Success, NotFound, Conflict, Invalid }
public sealed record RepairPlanCommand(decimal Price, DateTimeOffset PlannedStartUtc, DateTimeOffset EstimatedFinishUtc, bool InspectionPerformed, bool CustomerAgreed = false, string? PriceReason = null);
public sealed record TransitionCommand(AppointmentStatus Target, bool InspectionPerformed, bool PaymentReceived, bool VehicleHandedOver);
public sealed record ScheduleRepairCommand(DateOnly Start, int DurationDays, decimal Price, bool InspectionPerformed, string? PriceReason = null);
public sealed record PauseRepairCommand(string Reason, int DurationDays, bool WorkplaceReleased);

public interface IAdminAppointmentCommands
{
    Task<AdminAppointmentResult> PutPlanAsync(Guid id, uint version, string actorId, RepairPlanCommand command, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> ScheduleAsync(Guid id, uint version, string actorId, ScheduleRepairCommand command, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> TransitionAsync(Guid id, uint version, string actorId, TransitionCommand command, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> CancelAsync(Guid id, uint version, string actorId, bool confirmed, string reason, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> PauseAsync(Guid id, uint version, string actorId, PauseRepairCommand command, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> ResumeAsync(Guid id, uint version, string actorId, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> DelayAsync(Guid id, uint version, string actorId, DateOnly finish, string confirmationToken, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> AcceptEarlierAsync(Guid id, uint version, string actorId, Guid? pauseId, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> CorrectStatusAsync(Guid id, uint version, string actorId, AppointmentStatus target, string reason, bool confirmed, DateTimeOffset? inspectionStartUtc, CancellationToken cancellationToken);
}

/// <summary>Describes writes staged together with one appointment change.</summary>
public sealed record AdminAppointmentMutation(AdminAppointmentResult Result, AppointmentStatusEvent? StatusEvent = null,
    PaymentHandoverConfirmation? Confirmation = null, bool PriceChanged = false, bool DatesChanged = false, RepairPause? Pause = null,
    string? DelayToken = null, Guid? OfferPauseId = null, bool EarlyOffer = false, RepairPriceChange? PriceChange = null);

/// <summary>Loads and locks an appointment and commits all staged writes atomically.</summary>
public interface IAdminAppointmentStore
{
    Task<AdminAppointmentResult> ExecuteAsync(Guid id, uint version, string actorId,
        Func<Appointment, DateTimeOffset, AdminAppointmentMutation> mutate, CancellationToken cancellationToken);
}
