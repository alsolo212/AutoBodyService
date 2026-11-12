namespace AutoBodyService.Domain.Appointments;

/// <summary>
/// Stores the price and planned dates agreed after an initial inspection.
/// </summary>
public sealed class RepairPlan
{
    public RepairPlan(
        Guid appointmentId,
        decimal agreedPrice,
        DateTimeOffset plannedStartUtc,
        DateTimeOffset estimatedFinishUtc,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        AppointmentId = appointmentId;
        Currency = "UAH";
        Update(agreedPrice, plannedStartUtc, estimatedFinishUtc, updatedAtUtc);
    }

    public Guid AppointmentId { get; }

    public decimal AgreedPrice { get; private set; }

    public string Currency { get; }

    public DateTimeOffset PlannedStartUtc { get; private set; }

    public DateTimeOffset EstimatedFinishUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    internal void Update(decimal agreedPrice, DateTimeOffset plannedStartUtc, DateTimeOffset estimatedFinishUtc, DateTimeOffset updatedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(agreedPrice);
        EnsureUtc(plannedStartUtc, nameof(plannedStartUtc));
        EnsureUtc(estimatedFinishUtc, nameof(estimatedFinishUtc));
        EnsureUtc(updatedAtUtc, nameof(updatedAtUtc));
        if (estimatedFinishUtc < plannedStartUtc)
            throw new ArgumentException("Estimated finish time cannot be earlier than the planned start time.", nameof(estimatedFinishUtc));

        AgreedPrice = agreedPrice;
        PlannedStartUtc = plannedStartUtc;
        EstimatedFinishUtc = estimatedFinishUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Time must use the UTC offset.", parameterName);
        }
    }
}
