namespace AutoBodyService.Application.Employees;

/// <summary>Directory entry. Assignment data will be added when assignment storage exists.</summary>
public sealed record EmployeeDetails(Guid Id, string FullName, string Specialization, bool IsActive, uint Version);
public sealed record EmployeeChange(string? FullName, string? Specialization, bool? IsActive);
public enum EmployeeUpdateResult { Success, NotFound, Conflict }

public interface IEmployeeDirectory
{
    Task<IReadOnlyList<EmployeeDetails>> ListAsync(CancellationToken cancellationToken);
    Task<EmployeeDetails?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<EmployeeDetails> CreateAsync(string fullName, string specialization, bool isActive, CancellationToken cancellationToken);
    Task<(EmployeeUpdateResult Result, EmployeeDetails? Employee)> UpdateAsync(Guid id, uint version, EmployeeChange change, CancellationToken cancellationToken);
}
