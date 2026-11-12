namespace AutoBodyService.Domain.Appointments;

/// <summary>The purpose for which staff recorded a manual contact.</summary>
public enum ManualContactReason { NewAppointment, ScheduleConflictOrReschedule, Agreement, ReadyForPayment }

/// <summary>A staff member's record of manual contact, not proof of message delivery.</summary>
public sealed class ManualContactEvent
{
    private ManualContactEvent() { ActorId = null!; }
    public ManualContactEvent(Guid appointmentId, ManualContactReason reason, string actorId, DateTimeOffset recordedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (recordedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Time must use UTC.", nameof(recordedAtUtc));
        Id = Guid.NewGuid(); AppointmentId = appointmentId; Reason = reason; ActorId = actorId; RecordedAtUtc = recordedAtUtc;
    }
    public Guid Id { get; private set; }
    public Guid AppointmentId { get; private set; }
    public ManualContactReason Reason { get; private set; }
    public string ActorId { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
}
