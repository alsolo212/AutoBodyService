using AutoBodyService.Application.Employees;
using AutoBodyService.Domain.Employees;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Employees;

public sealed class EmployeeDirectory(ApplicationDbContext db) : IEmployeeDirectory
{
    public async Task<IReadOnlyList<EmployeeDetails>> ListAsync(CancellationToken cancellationToken)
    {
        var employees = await db.Employees.AsNoTracking().OrderBy(e => e.FullName).ThenBy(e => e.Id)
            .Select(e => new EmployeeDetails(e.Id, e.FullName, e.Specialization, e.IsActive, EF.Property<uint>(e, "Version")))
            .ToListAsync(cancellationToken);
        return await IncludeCurrentAssignmentsAsync(employees, cancellationToken);
    }

    public async Task<EmployeeDetails?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().Where(e => e.Id == id)
            .Select(e => new EmployeeDetails(e.Id, e.FullName, e.Specialization, e.IsActive, EF.Property<uint>(e, "Version")))
            .SingleOrDefaultAsync(cancellationToken);
        if (employee is null) return null;
        return (await IncludeCurrentAssignmentsAsync([employee], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<EmployeeDetails>> IncludeCurrentAssignmentsAsync(IReadOnlyList<EmployeeDetails> employees, CancellationToken cancellationToken)
    {
        if (employees.Count == 0) return employees;
        var ids = employees.Select(e => e.Id).ToArray();
        var rows = await db.EmployeeAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.EmployeeId) && a.ReleasedAtUtc == null)
            .OrderBy(a => a.AssignedAtUtc).ThenBy(a => a.Id)
            .Select(a => new { a.EmployeeId, Assignment = new EmployeeCurrentAssignment(a.Id, a.AppointmentId) })
            .ToListAsync(cancellationToken);
        var byEmployee = rows.ToLookup(row => row.EmployeeId, row => row.Assignment);
        return employees.Select(e => e with { CurrentAssignments = byEmployee[e.Id].ToArray() }).ToArray();
    }

    public async Task<EmployeeDetails> CreateAsync(string fullName, string specialization, bool isActive, CancellationToken cancellationToken)
    {
        var employee = new Employee(Guid.NewGuid(), fullName, specialization, isActive);
        db.Employees.Add(employee);
        await db.SaveChangesAsync(cancellationToken);
        return Details(employee);
    }

    public async Task<(EmployeeUpdateResult Result, EmployeeDetails? Employee)> UpdateAsync(Guid id, uint version, EmployeeChange change, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (employee is null) return (EmployeeUpdateResult.NotFound, null);
        var entry = db.Entry(employee);
        if (entry.Property<uint>("Version").CurrentValue != version) return (EmployeeUpdateResult.Conflict, null);
        employee.Update(change.FullName ?? employee.FullName, change.Specialization ?? employee.Specialization, change.IsActive ?? employee.IsActive);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return (EmployeeUpdateResult.Conflict, null); }
        return (EmployeeUpdateResult.Success, (await IncludeCurrentAssignmentsAsync([Details(employee)], cancellationToken))[0]);
    }

    private EmployeeDetails Details(Employee employee) => new(employee.Id, employee.FullName, employee.Specialization,
        employee.IsActive, db.Entry(employee).Property<uint>("Version").CurrentValue);
}
