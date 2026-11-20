namespace AutoBodyService.Application.Appointments;

public sealed record CreateAppointmentCommand(string OwnerId, DateTimeOffset InspectionStartUtc, string? Vin, string Make, string Model, string ProblemDescription,
    string? GuestName = null, string? GuestPhone = null);
public enum CreateAppointmentStatus { Created, Invalid, Unavailable, Unauthorized, DailyLimit }
public sealed record CreateAppointmentResult(CreateAppointmentStatus Status, Guid? Id = null, DateTimeOffset? InspectionStartUtc = null);

public interface ICreateAppointmentService
{
    Task<CreateAppointmentResult> CreateAsync(CreateAppointmentCommand command, CancellationToken cancellationToken);
}

public interface IAppointmentCreationStore
{
    Task<CreateAppointmentResult> SaveIfAvailableAsync(CreateAppointmentCommand command, CancellationToken cancellationToken);
}
