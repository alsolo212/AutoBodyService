namespace AutoBodyService.Application.Employees;

/// <summary>Minimal information needed to navigate to a currently assigned order.</summary>
public sealed record EmployeeCurrentAssignment(Guid Id, Guid AppointmentId);
/// <summary>Directory entry with current, unreleased assignments.</summary>
public sealed record EmployeeDetails(Guid Id, string FullName, string Specialization, bool IsActive, uint Version)
{
    public IReadOnlyList<EmployeeCurrentAssignment> CurrentAssignments { get; init; } = [];
}
public sealed record EmployeeChange(string? FullName, string? Specialization, bool? IsActive);
public enum EmployeeUpdateResult { Success, NotFound, Conflict }

public interface IEmployeeDirectory
{
    Task<IReadOnlyList<EmployeeDetails>> ListAsync(CancellationToken cancellationToken);
    Task<EmployeeDetails?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<EmployeeDetails> CreateAsync(string fullName, string specialization, bool isActive, CancellationToken cancellationToken);
    Task<(EmployeeUpdateResult Result, EmployeeDetails? Employee)> UpdateAsync(Guid id, uint version, EmployeeChange change, CancellationToken cancellationToken);
}
