namespace AutoBodyService.Application.Assignments;

/// <summary>Administrative assignment history including the actor and historical worker details.</summary>
public sealed record AssignmentDetails(Guid Id, Guid AppointmentId, Guid EmployeeId, string Name, string Specialization,
    string AssignedById, string AssignedByName, DateTimeOffset AssignedAtUtc, DateTimeOffset? ReleasedAtUtc, string? ReleasedById, string? ReleasedByName);
public enum AssignmentResult { Success, NotFound, Conflict }
public interface IEmployeeAssignments
{
    Task<IReadOnlyList<AssignmentDetails>> ListAsync(Guid? appointmentId, Guid? employeeId, bool? active, CancellationToken cancellationToken);
    Task<AssignmentDetails?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<(AssignmentResult Result, AssignmentDetails? Assignment)> AssignAsync(Guid appointmentId, Guid employeeId, string actorId, CancellationToken cancellationToken);
    Task<AssignmentResult> ReleaseAsync(Guid id, string actorId, CancellationToken cancellationToken);
}
