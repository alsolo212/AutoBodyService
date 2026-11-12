using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Appointments;

/// <summary>Optional categories of a customer's appointments.</summary>
public enum AppointmentCategory { Active, Completed, Cancelled }

/// <summary>Customer-safe overview of an appointment.</summary>
public sealed record AppointmentSummary(Guid Id, string Make, string Model, string MaskedVin,
    DateTimeOffset InspectionStartUtc, AppointmentStatus Status, AgreedRepairDetails? RepairPlan);
/// <summary>Price and dates agreed after inspection.</summary>
public sealed record AgreedRepairDetails(decimal AgreedPrice, string Currency, DateTimeOffset PlannedStartUtc, DateTimeOffset EstimatedFinishUtc);
/// <summary>Participant identity intended for the customer, without assignment internals.</summary>
public sealed record AppointmentParticipant(string Name, string Specialization);
/// <summary>Customer-visible status transition.</summary>
public sealed record CustomerStatusHistory(AppointmentStatus? FromStatus, AppointmentStatus ToStatus, DateTimeOffset ChangedAtUtc);
/// <summary>Administrative status transition including its actor and reason.</summary>
public sealed record AdminStatusHistory(AppointmentStatus? FromStatus, AppointmentStatus ToStatus, DateTimeOffset ChangedAtUtc, string ActorName, string? Reason);
/// <summary>A recorded manual interaction, not a delivery receipt.</summary>
public sealed record AdminManualContact(ManualContactReason Reason, string ActorName, DateTimeOffset RecordedAtUtc);
/// <summary>Server-recorded confirmation of payment and handover.</summary>
public sealed record AdminPaymentConfirmation(string ActorName, DateTimeOffset PaidAtUtc, DateTimeOffset HandedOverAtUtc);
/// <summary>Assignment history with the actor and worker snapshot.</summary>
public sealed record AdminAssignmentDetails(Guid Id, Guid EmployeeId, string Name, string Specialization, string AssignedById,
    string AssignedByName, DateTimeOffset AssignedAtUtc, DateTimeOffset? ReleasedAtUtc, string? ReleasedById, string? ReleasedByName);
/// <summary>Owner-visible details, including the workers who participated.</summary>
public sealed record CustomerAppointmentDetails(Guid Id, string Vin, string Make, string Model, string ProblemDescription,
    DateTimeOffset InspectionStartUtc, AppointmentStatus Status, IReadOnlyList<CustomerStatusHistory> History,
    AgreedRepairDetails? RepairPlan, IReadOnlyList<AppointmentParticipant> Participants);
/// <summary>Administrative details of an appointment and its customer.</summary>
public sealed record AdminAppointmentDetails(Guid Id, string Vin, string Make, string Model, string ProblemDescription,
    DateTimeOffset InspectionStartUtc, AppointmentStatus Status, string CustomerName, string CustomerPhone,
    IReadOnlyList<AdminStatusHistory> History, AgreedRepairDetails? RepairPlan, IReadOnlyList<AppointmentParticipant> Participants, uint Version,
    AdminPaymentConfirmation? PaymentConfirmation, IReadOnlyList<AdminManualContact> ManualContacts, IReadOnlyList<AdminAssignmentDetails> Assignments);

public interface IAppointmentQueries
{
    Task<IReadOnlyList<AppointmentSummary>> GetMineAsync(string userId, AppointmentCategory? category, CancellationToken cancellationToken);
    Task<CustomerAppointmentDetails?> GetOwnAsync(Guid id, string userId, CancellationToken cancellationToken);
    Task<AdminAppointmentDetails?> GetForAdminAsync(Guid id, CancellationToken cancellationToken);
}
