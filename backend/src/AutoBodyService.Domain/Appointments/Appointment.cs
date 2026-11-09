namespace AutoBodyService.Domain.Appointments;

/// <summary>
/// Represents a customer's appointment for an initial vehicle inspection.
/// </summary>
public sealed class Appointment
{
    private readonly List<AppointmentStatusEvent> _statusEvents = [];

    // EF materialization must not create a second initial event.
    private Appointment()
    {
        UserId = Vin = Make = Model = ProblemDescription = null!;
    }

    public Appointment(
        Guid id,
        string userId,
        DateTimeOffset inspectionStartUtc,
        string vin,
        string make,
        string model,
        string problemDescription,
        DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vin);
        ArgumentException.ThrowIfNullOrWhiteSpace(make);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(problemDescription);

        vin = vin.Trim();
        if (vin.Length != 17 || vin.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'H' or >= 'J' and <= 'N' or 'P' or >= 'R' and <= 'Z')))
        {
            throw new ArgumentException("VIN must contain exactly 17 permitted uppercase letters or digits.", nameof(vin));
        }

        if (inspectionStartUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Inspection time must use the UTC offset.", nameof(inspectionStartUtc));
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Creation time must use the UTC offset.", nameof(createdAtUtc));
        }

        Id = id;
        UserId = userId;
        InspectionStartUtc = inspectionStartUtc;
        Vin = vin;
        Make = make.Trim();
        Model = model.Trim();
        ProblemDescription = problemDescription.Trim();
        CreatedAtUtc = createdAtUtc;
        Status = AppointmentStatus.AwaitingInspection;
        _statusEvents.Add(new AppointmentStatusEvent(Guid.NewGuid(), Id, null, Status, UserId, CreatedAtUtc, null));
    }

    public Guid Id { get; }

    public string UserId { get; }

    public DateTimeOffset InspectionStartUtc { get; }

    public string Vin { get; }

    public string Make { get; }

    public string Model { get; }

    public string ProblemDescription { get; }

    public AppointmentStatus Status { get; private set; }

    public RepairPlan? RepairPlan { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyCollection<AppointmentStatusEvent> StatusEvents => _statusEvents.AsReadOnly();

    public void Schedule(RepairPlan repairPlan)
    {
        ArgumentNullException.ThrowIfNull(repairPlan);

        if (Status != AppointmentStatus.AwaitingInspection)
        {
            throw new InvalidOperationException(
                "Only an appointment awaiting inspection can be scheduled.");
        }

        if (repairPlan.AppointmentId != Id)
        {
            throw new ArgumentException(
                "The repair plan must belong to this appointment.",
                nameof(repairPlan));
        }

        RepairPlan = repairPlan;
        Status = AppointmentStatus.Scheduled;
    }
}
