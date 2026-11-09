namespace AutoBodyService.Application.Appointments;

public enum CancelAppointmentResult { Cancelled, NotFound, Conflict }

public interface ICancelAppointmentService
{
    Task<CancelAppointmentResult> CancelOwnAsync(Guid appointmentId, string ownerId, CancellationToken cancellationToken);
}
