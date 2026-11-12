using AutoBodyService.Application.Employees;
using AutoBodyService.Domain.Employees;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Employees;

public sealed class EmployeeDirectory(ApplicationDbContext db) : IEmployeeDirectory
{
    public async Task<IReadOnlyList<EmployeeDetails>> ListAsync(CancellationToken cancellationToken) =>
        await db.Employees.AsNoTracking().OrderBy(e => e.FullName).ThenBy(e => e.Id)
            .Select(e => new EmployeeDetails(e.Id, e.FullName, e.Specialization, e.IsActive, EF.Property<uint>(e, "Version")))
            .ToListAsync(cancellationToken);

    public Task<EmployeeDetails?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Employees.AsNoTracking().Where(e => e.Id == id)
            .Select(e => new EmployeeDetails(e.Id, e.FullName, e.Specialization, e.IsActive, EF.Property<uint>(e, "Version")))
            .SingleOrDefaultAsync(cancellationToken);

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
        return (EmployeeUpdateResult.Success, Details(employee));
    }

    private EmployeeDetails Details(Employee employee) => new(employee.Id, employee.FullName, employee.Specialization,
        employee.IsActive, db.Entry(employee).Property<uint>("Version").CurrentValue);
}
