namespace AutoBodyService.Domain.Appointments;

/// <summary>A recorded interruption. Ended interruptions remain part of the order history.</summary>
public sealed class RepairPause
{
    private RepairPause() { Reason = ActorId = null!; }
    public RepairPause(Guid appointmentId, string reason, int durationDays, bool workplaceReleased, DateOnly startedOn, string actorId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (reason.Trim().Length > 500 || durationDays <= 0 || (long)startedOn.DayNumber + durationDays > DateOnly.MaxValue.DayNumber)
            throw new ArgumentException("Invalid pause duration or reason.");
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("Time must use UTC.", nameof(now));
        Id = Guid.NewGuid(); AppointmentId = appointmentId; Reason = reason.Trim(); DurationDays = durationDays;
        WorkplaceReleased = workplaceReleased; StartedOn = startedOn; ExpectedReturn = startedOn.AddDays(durationDays);
        ActorId = actorId; StartedAtUtc = now;
    }
    public Guid Id { get; private set; }
    public Guid AppointmentId { get; private set; }
    public string Reason { get; private set; }
    public int DurationDays { get; private set; }
    public bool WorkplaceReleased { get; private set; }
    public DateOnly StartedOn { get; private set; }
    public DateOnly ExpectedReturn { get; private set; }
    public string ActorId { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? EndedAtUtc { get; private set; }
    public string? EndedById { get; private set; }
    public void End(string actorId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (EndedAtUtc is not null || now.Offset != TimeSpan.Zero || now < StartedAtUtc) throw new InvalidOperationException("Pause cannot be ended.");
        EndedAtUtc = now; EndedById = actorId;
    }
}
