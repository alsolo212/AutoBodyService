namespace AutoBodyService.Domain.Appointments;

/// <summary>A historical status change, including the initial creation of an appointment.</summary>
public sealed class AppointmentStatusEvent
{
    private AppointmentStatusEvent() { ActorId = null!; }

    internal AppointmentStatusEvent(Guid id, Guid appointmentId, AppointmentStatus? fromStatus,
        AppointmentStatus toStatus, string actorId, DateTimeOffset changedAtUtc, string? reason)
    {
        Id = id;
        AppointmentId = appointmentId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ActorId = actorId;
        ChangedAtUtc = changedAtUtc;
        Reason = reason;
    }

    public Guid Id { get; private set; }
    public Guid AppointmentId { get; private set; }
    public AppointmentStatus? FromStatus { get; private set; }
    public AppointmentStatus ToStatus { get; private set; }
    public string ActorId { get; private set; }
    public DateTimeOffset ChangedAtUtc { get; private set; }
    public string? Reason { get; private set; }
}
