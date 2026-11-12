using AutoBodyService.Domain.Appointments;
using AutoBodyService.Domain.Employees;

namespace AutoBodyService.Domain.Assignments;

/// <summary>A historical period of a worker's participation in one repair.</summary>
public sealed class EmployeeAssignment
{
    private EmployeeAssignment() { AssignedById = NameSnapshot = SpecializationSnapshot = null!; }
    public Guid Id { get; private set; }
    public Guid AppointmentId { get; private set; }
    public Guid EmployeeId { get; private set; }
    public string AssignedById { get; private set; }
    public DateTimeOffset AssignedAtUtc { get; private set; }
    public DateTimeOffset? ReleasedAtUtc { get; private set; }
    public string NameSnapshot { get; private set; }
    public string SpecializationSnapshot { get; private set; }

    public static EmployeeAssignment Assign(Appointment appointment, Employee employee, string actorId, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(appointment);
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (atUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Time must use UTC.", nameof(atUtc));
        if (appointment.Status != AppointmentStatus.InProgress || !employee.IsActive)
            throw new InvalidOperationException("Only active workers can be assigned to repairs in progress.");
        return new EmployeeAssignment { Id = Guid.NewGuid(), AppointmentId = appointment.Id, EmployeeId = employee.Id,
            AssignedById = actorId, AssignedAtUtc = atUtc, NameSnapshot = employee.FullName, SpecializationSnapshot = employee.Specialization };
    }

    public void Release(DateTimeOffset atUtc)
    {
        if (atUtc.Offset != TimeSpan.Zero || atUtc < AssignedAtUtc) throw new ArgumentException("Release time must be UTC and not precede assignment.", nameof(atUtc));
        if (ReleasedAtUtc is not null) throw new InvalidOperationException("Assignment is already released.");
        ReleasedAtUtc = atUtc;
    }
}
