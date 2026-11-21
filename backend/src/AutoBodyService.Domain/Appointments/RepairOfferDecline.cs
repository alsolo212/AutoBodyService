namespace AutoBodyService.Domain.Appointments;

/// <summary>Records an explicit refusal for a candidate during one pause.</summary>
public sealed class RepairOfferDecline
{
    private RepairOfferDecline() { ActorId = null!; }
    public RepairOfferDecline(Guid pauseId, Guid appointmentId, string actorId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(pauseId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("Time must use UTC.", nameof(now));
        Id = Guid.NewGuid(); PauseId = pauseId; AppointmentId = appointmentId; ActorId = actorId; RecordedAtUtc = now;
    }
    public Guid Id { get; private set; }
    public Guid PauseId { get; private set; }
    public Guid AppointmentId { get; private set; }
    public string ActorId { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
}
