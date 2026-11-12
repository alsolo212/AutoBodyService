using AutoBodyService.Application.Assignments;
using AutoBodyService.Domain.Assignments;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoBodyService.Infrastructure.Assignments;

public sealed class EmployeeAssignments(ApplicationDbContext db, TimeProvider clock) : IEmployeeAssignments
{
    public async Task<IReadOnlyList<AssignmentDetails>> ListAsync(Guid? appointmentId, Guid? employeeId, bool? active, CancellationToken cancellationToken)
    {
        var query = db.EmployeeAssignments.AsNoTracking().AsQueryable();
        if (appointmentId.HasValue) query = query.Where(a => a.AppointmentId == appointmentId.Value);
        if (employeeId.HasValue) query = query.Where(a => a.EmployeeId == employeeId.Value);
        if (active.HasValue) query = query.Where(a => (a.ReleasedAtUtc == null) == active.Value);
        return await (from a in query
                      join actor in db.Users.AsNoTracking() on a.AssignedById equals actor.Id
                      orderby a.AssignedAtUtc descending, a.Id
                      select new AssignmentDetails(a.Id, a.AppointmentId, a.EmployeeId, a.NameSnapshot, a.SpecializationSnapshot,
                          a.AssignedById, actor.Name, a.AssignedAtUtc, a.ReleasedAtUtc)).ToListAsync(cancellationToken);
    }

    public Task<AssignmentDetails?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        (from a in db.EmployeeAssignments.AsNoTracking()
         join actor in db.Users.AsNoTracking() on a.AssignedById equals actor.Id
         where a.Id == id
         select new AssignmentDetails(a.Id, a.AppointmentId, a.EmployeeId, a.NameSnapshot, a.SpecializationSnapshot,
             a.AssignedById, actor.Name, a.AssignedAtUtc, a.ReleasedAtUtc)).SingleOrDefaultAsync(cancellationToken);

    public async Task<(AssignmentResult Result, AssignmentDetails? Assignment)> AssignAsync(Guid appointmentId, Guid employeeId, string actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Lock ordering is appointment first, then employee, in every assignment operation.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {appointmentId} FOR UPDATE", cancellationToken);
        var appointment = await db.Appointments.SingleOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment is null) return (AssignmentResult.NotFound, null);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Employees\" WHERE \"Id\" = {employeeId} FOR UPDATE", cancellationToken);
        var employee = await db.Employees.SingleOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        if (employee is null) return (AssignmentResult.NotFound, null);
        if (appointment.Status != AppointmentStatus.InProgress || !employee.IsActive) return (AssignmentResult.Conflict, null);
        if (await db.EmployeeAssignments.AnyAsync(a => a.AppointmentId == appointmentId && a.EmployeeId == employeeId && a.ReleasedAtUtc == null, cancellationToken))
            return (AssignmentResult.Conflict, null);
        var assignment = EmployeeAssignment.Assign(appointment, employee, actorId, clock.GetUtcNow());
        db.EmployeeAssignments.Add(assignment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            var result = await GetAsync(assignment.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (AssignmentResult.Success, result);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_EmployeeAssignments_AppointmentId_EmployeeId" })
        { return (AssignmentResult.Conflict, null); }
    }

    public async Task<AssignmentResult> ReleaseAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var appointmentId = await db.EmployeeAssignments.AsNoTracking().Where(a => a.Id == id).Select(a => (Guid?)a.AppointmentId).SingleOrDefaultAsync(cancellationToken);
        if (appointmentId is null) return AssignmentResult.NotFound;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {appointmentId.Value} FOR UPDATE", cancellationToken);
        var assignment = await db.EmployeeAssignments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (assignment is null) return AssignmentResult.NotFound;
        if (assignment.ReleasedAtUtc is not null) return AssignmentResult.Conflict;
        assignment.Release(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AssignmentResult.Success;
    }
}
