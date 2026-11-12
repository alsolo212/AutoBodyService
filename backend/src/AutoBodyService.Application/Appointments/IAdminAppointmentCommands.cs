using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Appointments;

public enum AdminAppointmentResult { Success, NotFound, Conflict, Invalid }
public sealed record RepairPlanCommand(decimal Price, DateTimeOffset PlannedStartUtc, DateTimeOffset EstimatedFinishUtc, bool InspectionPerformed);
public sealed record TransitionCommand(AppointmentStatus Target, bool InspectionPerformed, bool PaymentReceived, bool VehicleHandedOver);

public interface IAdminAppointmentCommands
{
    Task<AdminAppointmentResult> PutPlanAsync(Guid id, uint version, string actorId, RepairPlanCommand command, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> TransitionAsync(Guid id, uint version, string actorId, TransitionCommand command, CancellationToken cancellationToken);
    Task<AdminAppointmentResult> CancelAsync(Guid id, uint version, string actorId, bool confirmed, string reason, CancellationToken cancellationToken);
}

/// <summary>Describes writes staged together with one appointment change.</summary>
public sealed record AdminAppointmentMutation(AdminAppointmentResult Result, AppointmentStatusEvent? StatusEvent = null,
    PaymentHandoverConfirmation? Confirmation = null, bool PriceChanged = false, bool DatesChanged = false);

/// <summary>Loads and locks an appointment and commits all staged writes atomically.</summary>
public interface IAdminAppointmentStore
{
    Task<AdminAppointmentResult> ExecuteAsync(Guid id, uint version, string actorId,
        Func<Appointment, DateTimeOffset, AdminAppointmentMutation> mutate, CancellationToken cancellationToken);
}
