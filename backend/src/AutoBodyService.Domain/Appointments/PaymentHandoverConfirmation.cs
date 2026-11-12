namespace AutoBodyService.Domain.Appointments;

/// <summary>Server-recorded confirmation of both offline payment and vehicle handover.</summary>
public sealed class PaymentHandoverConfirmation
{
    private PaymentHandoverConfirmation() { ActorId = null!; }

    public PaymentHandoverConfirmation(Guid appointmentId, string actorId, DateTimeOffset confirmedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (confirmedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Time must use UTC.", nameof(confirmedAtUtc));
        AppointmentId = appointmentId;
        ActorId = actorId;
        PaidAtUtc = confirmedAtUtc;
        HandedOverAtUtc = confirmedAtUtc;
    }

    public Guid AppointmentId { get; private set; }
    public string ActorId { get; private set; }
    public DateTimeOffset PaidAtUtc { get; private set; }
    public DateTimeOffset HandedOverAtUtc { get; private set; }
}
