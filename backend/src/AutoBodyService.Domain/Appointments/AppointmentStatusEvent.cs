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

    public static AppointmentStatusEvent ForTransition(Guid appointmentId, AppointmentStatus from, AppointmentStatus to,
        string actorId, DateTimeOffset changedAtUtc, string? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (changedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Time must use UTC.", nameof(changedAtUtc));
        if ((from, to) is not ((AppointmentStatus.AwaitingInspection, AppointmentStatus.Scheduled)
            or (AppointmentStatus.Scheduled, AppointmentStatus.InProgress)
            or (AppointmentStatus.InProgress, AppointmentStatus.AwaitingPayment)
            or (AppointmentStatus.AwaitingPayment, AppointmentStatus.Completed)
            or (AppointmentStatus.AwaitingInspection or AppointmentStatus.Scheduled or AppointmentStatus.InProgress, AppointmentStatus.Cancelled)))
            throw new ArgumentException("Invalid status transition.", nameof(to));
        if (to == AppointmentStatus.Cancelled) ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        else if (reason is not null) throw new ArgumentException("Only cancellation has a reason.", nameof(reason));
        return new AppointmentStatusEvent(Guid.NewGuid(), appointmentId, from, to, actorId, changedAtUtc, reason?.Trim());
    }
}
