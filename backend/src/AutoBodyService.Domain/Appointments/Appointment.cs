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
        CreatedById = Make = Model = ProblemDescription = null!;
    }

    public Appointment(
        Guid id,
        string userId,
        DateTimeOffset inspectionStartUtc,
        string? vin,
        string make,
        string model,
        string problemDescription,
        DateTimeOffset createdAtUtc) : this(id, userId, userId, null, null, inspectionStartUtc, vin, make, model, problemDescription, createdAtUtc)
    { }

    public static Appointment ForGuest(Guid id, string createdById, string customerName, string customerPhone,
        DateTimeOffset inspectionStartUtc, string? vin, string make, string model, string problemDescription, DateTimeOffset createdAtUtc) =>
        new(id, null, createdById, customerName, customerPhone, inspectionStartUtc, vin, make, model, problemDescription, createdAtUtc);

    private Appointment(Guid id, string? userId, string createdById, string? customerName, string? customerPhone,
        DateTimeOffset inspectionStartUtc, string? vin, string make, string model, string problemDescription, DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdById);
        if (userId is null && (string.IsNullOrWhiteSpace(customerName) || string.IsNullOrWhiteSpace(customerPhone)))
            throw new ArgumentException("Guest contact details are required.");
        if (userId is not null && (string.IsNullOrWhiteSpace(userId) || customerName is not null || customerPhone is not null))
            throw new ArgumentException("An appointment must have one customer identity.");
        ArgumentException.ThrowIfNullOrWhiteSpace(make);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!ProblemDescriptionValidator.IsValid(problemDescription))
            throw new ArgumentException("A text description of the problem is required.", nameof(problemDescription));

        vin = string.IsNullOrWhiteSpace(vin) ? null : vin.Trim();
        if (vin is not null && (vin.Length != 17 || vin.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'H' or >= 'J' and <= 'N' or 'P' or >= 'R' and <= 'Z'))))
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
        CreatedById = createdById;
        CustomerName = customerName?.Trim();
        CustomerPhone = customerPhone;
        InspectionStartUtc = inspectionStartUtc;
        Vin = vin;
        Make = make.Trim();
        Model = model.Trim();
        ProblemDescription = problemDescription.Trim();
        CreatedAtUtc = createdAtUtc;
        Status = AppointmentStatus.AwaitingInspection;
        _statusEvents.Add(new AppointmentStatusEvent(Guid.NewGuid(), Id, null, Status, CreatedById, CreatedAtUtc, null));
    }

    public Guid Id { get; }

    public string? UserId { get; }
    public string CreatedById { get; }
    public string? CustomerName { get; }
    public string? CustomerPhone { get; }

    public DateTimeOffset InspectionStartUtc { get; }

    public string? Vin { get; }

    public string Make { get; }

    public string Model { get; }

    public string ProblemDescription { get; }

    public AppointmentStatus Status { get; private set; }

    public bool CanRecordManualContact() => Status is not (AppointmentStatus.Completed or AppointmentStatus.Cancelled);

    public RepairPlan? RepairPlan { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyCollection<AppointmentStatusEvent> StatusEvents => _statusEvents.AsReadOnly();

    /// <summary>Cancels a future inspection after the caller has verified ownership.</summary>
    public AppointmentStatusEvent? CancelByOwner(DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Time must use the UTC offset.", nameof(nowUtc));
        if (UserId is null || Status != AppointmentStatus.AwaitingInspection || InspectionStartUtc <= nowUtc)
            return null;

        var statusEvent = new AppointmentStatusEvent(Guid.NewGuid(), Id, Status, AppointmentStatus.Cancelled, UserId, nowUtc, null);
        Status = AppointmentStatus.Cancelled;
        _statusEvents.Add(statusEvent);
        return statusEvent;
    }

    public void Schedule(RepairPlan repairPlan, bool inspectionPerformed)
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

        if (!inspectionPerformed)
            throw new InvalidOperationException("The inspection must be performed before scheduling.");

        RepairPlan = repairPlan;
        Status = AppointmentStatus.Scheduled;
    }

    /// <summary>Stores the agreement after inspection without advancing the status.</summary>
    public void AgreeRepairPlan(RepairPlan plan, bool inspectionPerformed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Status != AppointmentStatus.AwaitingInspection)
            throw new InvalidOperationException("An initial agreement requires an appointment awaiting inspection.");
        if (plan.AppointmentId != Id) throw new ArgumentException("The plan belongs to another appointment.", nameof(plan));
        if (!inspectionPerformed) throw new InvalidOperationException("The inspection must be performed before agreement.");
        if (RepairPlan is null) RepairPlan = plan;
        else
        {
            if (plan.UpdatedAtUtc < RepairPlan.UpdatedAtUtc) throw new ArgumentException("The agreement cannot be backdated.", nameof(plan));
            RepairPlan.Update(plan.AgreedPrice, plan.PlannedStartUtc, plan.EstimatedFinishUtc, plan.UpdatedAtUtc);
            RepairPlan.SetCalendarDatesOnly(plan.CalendarDatesOnly);
        }
    }

    public void StartRepair() => MoveFrom(AppointmentStatus.Scheduled, AppointmentStatus.InProgress);

    public void PauseRepair() => MoveFrom(AppointmentStatus.InProgress, AppointmentStatus.Paused);

    public void ResumeRepair() => MoveFrom(AppointmentStatus.Paused, AppointmentStatus.InProgress);

    public void ReviseRepairPlan(decimal agreedPrice, DateTimeOffset plannedStartUtc, DateTimeOffset estimatedFinishUtc, DateTimeOffset updatedAtUtc)
    {
        if (Status is not (AppointmentStatus.Scheduled or AppointmentStatus.InProgress or AppointmentStatus.Paused or AppointmentStatus.AwaitingPayment) || RepairPlan is null)
            throw new InvalidOperationException("Only a non-terminal appointment with an agreed plan can be revised.");
        if (updatedAtUtc < RepairPlan.UpdatedAtUtc)
            throw new ArgumentException("The update cannot precede the previous agreement.", nameof(updatedAtUtc));
        var datesChanged = plannedStartUtc != RepairPlan.PlannedStartUtc || estimatedFinishUtc != RepairPlan.EstimatedFinishUtc;
        RepairPlan.Update(agreedPrice, plannedStartUtc, estimatedFinishUtc, updatedAtUtc);
        if (datesChanged) RepairPlan.SetCalendarDatesOnly(false);
    }

    public void ReviseCalendarRepairPlan(decimal agreedPrice, DateTimeOffset startUtc, DateTimeOffset finishUtc, DateTimeOffset updatedAtUtc)
    {
        ReviseRepairPlan(agreedPrice, startUtc, finishUtc, updatedAtUtc);
        RepairPlan!.SetCalendarDatesOnly(true);
    }

    public void FinishRepair() => MoveFrom(AppointmentStatus.InProgress, AppointmentStatus.AwaitingPayment);

    public void Complete(bool paymentReceived, bool vehicleHandedOver)
    {
        if (Status != AppointmentStatus.AwaitingPayment)
            throw new InvalidOperationException("Only an appointment awaiting payment can be completed.");
        if (!paymentReceived || !vehicleHandedOver)
            throw new InvalidOperationException("Payment and vehicle handover must both be confirmed.");

        Status = AppointmentStatus.Completed;
    }

    /// <summary>Cancels a repair after the caller has verified operator authorization.</summary>
    public void CancelByAdmin(bool confirmed, string reason)
    {
        if (Status is not (AppointmentStatus.AwaitingInspection or AppointmentStatus.Scheduled or AppointmentStatus.InProgress or AppointmentStatus.Paused))
            throw new InvalidOperationException("This appointment cannot be cancelled.");
        if (!confirmed)
            throw new InvalidOperationException("Cancellation must be confirmed.");
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Status = AppointmentStatus.Cancelled;
    }

    private void MoveFrom(AppointmentStatus expected, AppointmentStatus next)
    {
        if (Status != expected)
            throw new InvalidOperationException("This status transition is not allowed.");
        Status = next;
    }
}
