namespace AutoBodyService.Domain.Appointments;

/// <summary>A customer-visible reason for changing an existing repair price.</summary>
public sealed class RepairPriceChange
{
    private RepairPriceChange() { Reason = ActorId = null!; }
    public RepairPriceChange(Guid appointmentId, decimal oldPrice, decimal newPrice, string reason, string actorId, DateTimeOffset atUtc)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (reason.Trim().Length > 1000 || oldPrice < 0 || newPrice < 0 || oldPrice == newPrice || atUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid price change.");
        Id = Guid.NewGuid(); AppointmentId = appointmentId; OldPrice = oldPrice; NewPrice = newPrice;
        Reason = reason.Trim(); ActorId = actorId; ChangedAtUtc = atUtc;
    }
    public Guid Id { get; private set; }
    public Guid AppointmentId { get; private set; }
    public decimal OldPrice { get; private set; }
    public decimal NewPrice { get; private set; }
    public string Reason { get; private set; }
    public string ActorId { get; private set; }
    public DateTimeOffset ChangedAtUtc { get; private set; }
}
