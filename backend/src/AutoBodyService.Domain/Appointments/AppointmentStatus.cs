namespace AutoBodyService.Domain.Appointments;

/// <summary>
/// Describes the approved stage of an appointment in its repair lifecycle.
/// </summary>
public enum AppointmentStatus
{
    AwaitingInspection,
    Scheduled,
    InProgress,
    AwaitingPayment,
    Completed,
    Cancelled
}
