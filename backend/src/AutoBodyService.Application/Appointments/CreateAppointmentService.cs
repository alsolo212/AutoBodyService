using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Appointments;

/// <summary>Validates the creation command before handing it to the transactional store.</summary>
public sealed class CreateAppointmentService(IAppointmentCreationStore store, TimeProvider clock) : ICreateAppointmentService
{
    public Task<CreateAppointmentResult> CreateAsync(CreateAppointmentCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.OwnerId) || command.InspectionStartUtc.Offset != TimeSpan.Zero ||
            command.InspectionStartUtc.Year is < 2 or > 9998)
            return Task.FromResult(new CreateAppointmentResult(CreateAppointmentStatus.Invalid));
        try
        {
            var appointment = new Appointment(Guid.NewGuid(), command.OwnerId, command.InspectionStartUtc,
                command.Vin, command.Make, command.Model, command.ProblemDescription, clock.GetUtcNow());
            return store.SaveIfAvailableAsync(appointment, cancellationToken);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new CreateAppointmentResult(CreateAppointmentStatus.Invalid));
        }
    }
}
