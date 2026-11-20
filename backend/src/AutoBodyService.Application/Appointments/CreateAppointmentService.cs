using AutoBodyService.Domain.Appointments;
using AutoBodyService.Application.Authentication;

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
            _ = Build(command, clock.GetUtcNow());
            return store.SaveIfAvailableAsync(command, cancellationToken);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new CreateAppointmentResult(CreateAppointmentStatus.Invalid));
        }
    }

    public static Appointment Build(CreateAppointmentCommand command, DateTimeOffset now)
    {
        if (command.GuestName is null && command.GuestPhone is null)
            return new Appointment(Guid.NewGuid(), command.OwnerId, command.InspectionStartUtc, command.Vin, command.Make, command.Model, command.ProblemDescription, now);
        var name = command.GuestName?.Trim();
        var phone = PhoneNumberNormalizer.Normalize(command.GuestPhone ?? "");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl) || phone is null)
            throw new ArgumentException("Invalid guest contact details.");
        return Appointment.ForGuest(Guid.NewGuid(), command.OwnerId, name, phone, command.InspectionStartUtc,
            command.Vin, command.Make, command.Model, command.ProblemDescription, now);
    }
}
