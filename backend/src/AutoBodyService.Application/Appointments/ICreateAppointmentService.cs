namespace AutoBodyService.Application.Appointments;

public sealed record CreateAppointmentCommand(string OwnerId, DateTimeOffset InspectionStartUtc, string Vin, string Make, string Model, string ProblemDescription);
public enum CreateAppointmentStatus { Created, Invalid, Unavailable, Unauthorized }
public sealed record CreateAppointmentResult(CreateAppointmentStatus Status, Guid? Id = null, DateTimeOffset? InspectionStartUtc = null);

public interface ICreateAppointmentService
{
    Task<CreateAppointmentResult> CreateAsync(CreateAppointmentCommand command, CancellationToken cancellationToken);
}

public interface IAppointmentCreationStore
{
    Task<CreateAppointmentResult> SaveIfAvailableAsync(AutoBodyService.Domain.Appointments.Appointment appointment, CancellationToken cancellationToken);
}
