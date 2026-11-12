using AutoBodyService.Application.Appointments;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class AppointmentQueries(ApplicationDbContext db) : IAppointmentQueries
{
    public async Task<IReadOnlyList<AppointmentSummary>> GetMineAsync(string userId, AppointmentCategory? category, CancellationToken cancellationToken)
    {
        var query = db.Appointments.AsNoTracking().Where(a => a.UserId == userId);
        query = category switch
        {
            AppointmentCategory.Active => query.Where(a => a.Status != AppointmentStatus.Completed && a.Status != AppointmentStatus.Cancelled),
            AppointmentCategory.Completed => query.Where(a => a.Status == AppointmentStatus.Completed),
            AppointmentCategory.Cancelled => query.Where(a => a.Status == AppointmentStatus.Cancelled),
            _ => query
        };
        var rows = await query.OrderByDescending(a => a.InspectionStartUtc).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.Make, a.Model, a.Vin, a.InspectionStartUtc, a.Status,
                Plan = a.RepairPlan == null ? null : new AgreedRepairDetails(a.RepairPlan.AgreedPrice, a.RepairPlan.Currency, a.RepairPlan.PlannedStartUtc, a.RepairPlan.EstimatedFinishUtc) }).ToListAsync(cancellationToken);
        return rows.Select(a => new AppointmentSummary(a.Id, a.Make, a.Model, new string('*', 13) + a.Vin[^4..], a.InspectionStartUtc, a.Status, a.Plan)).ToArray();
    }

    public async Task<CustomerAppointmentDetails?> GetOwnAsync(Guid id, string userId, CancellationToken cancellationToken)
    {
        var row = await db.Appointments.AsNoTracking().Where(a => a.Id == id && a.UserId == userId)
            .Select(a => new { a.Id, a.Vin, a.Make, a.Model, a.ProblemDescription, a.InspectionStartUtc, a.Status,
                Plan = a.RepairPlan == null ? null : new AgreedRepairDetails(a.RepairPlan.AgreedPrice, a.RepairPlan.Currency, a.RepairPlan.PlannedStartUtc, a.RepairPlan.EstimatedFinishUtc),
                History = a.StatusEvents.OrderBy(e => e.ChangedAtUtc).ThenBy(e => e.Id)
                    .Select(e => new CustomerStatusHistory(e.FromStatus, e.ToStatus, e.ChangedAtUtc)).ToList() })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var participants = await ParticipantsAsync(id, cancellationToken);
        return new CustomerAppointmentDetails(row.Id, row.Vin, row.Make, row.Model, row.ProblemDescription,
            row.InspectionStartUtc, row.Status, row.History, row.Plan, participants);
    }

    public async Task<AdminAppointmentDetails?> GetForAdminAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await (from a in db.Appointments.AsNoTracking()
                         join owner in db.Users.AsNoTracking() on a.UserId equals owner.Id
                         where a.Id == id
                         select new { a.Id, a.Vin, a.Make, a.Model, a.ProblemDescription, a.InspectionStartUtc, a.Status,
                             Version = EF.Property<uint>(a, "Version"),
                             Plan = a.RepairPlan == null ? null : new AgreedRepairDetails(a.RepairPlan.AgreedPrice, a.RepairPlan.Currency, a.RepairPlan.PlannedStartUtc, a.RepairPlan.EstimatedFinishUtc),
                             CustomerName = owner.Name, CustomerPhone = owner.PhoneNormalized })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var history = await (from e in db.AppointmentStatusEvents.AsNoTracking()
                             join actor in db.Users.AsNoTracking() on e.ActorId equals actor.Id
                             where e.AppointmentId == id
                             orderby e.ChangedAtUtc, e.Id
                             select new AdminStatusHistory(e.FromStatus, e.ToStatus, e.ChangedAtUtc, actor.Name, e.Reason))
            .ToListAsync(cancellationToken);
        var payment = await (from p in db.PaymentHandoverConfirmations.AsNoTracking()
                             join actor in db.Users.AsNoTracking() on p.ActorId equals actor.Id
                             where p.AppointmentId == id
                             select new AdminPaymentConfirmation(actor.Name, p.PaidAtUtc, p.HandedOverAtUtc)).SingleOrDefaultAsync(cancellationToken);
        var contacts = await (from e in db.ManualContactEvents.AsNoTracking()
                              join actor in db.Users.AsNoTracking() on e.ActorId equals actor.Id
                              where e.AppointmentId == id
                              orderby e.RecordedAtUtc descending, e.Id
                              select new AdminManualContact(e.Reason, actor.Name, e.RecordedAtUtc)).ToListAsync(cancellationToken);
        var assignments = await (from a in db.EmployeeAssignments.AsNoTracking()
                                 join actor in db.Users.AsNoTracking() on a.AssignedById equals actor.Id
                                 where a.AppointmentId == id
                                 orderby a.AssignedAtUtc, a.Id
                                 select new AdminAssignmentDetails(a.Id, a.EmployeeId, a.NameSnapshot, a.SpecializationSnapshot,
                                     a.AssignedById, actor.Name, a.AssignedAtUtc, a.ReleasedAtUtc, a.ReleasedById,
                                     db.Users.Where(u => u.Id == a.ReleasedById).Select(u => u.Name).SingleOrDefault())).ToListAsync(cancellationToken);
        var participants = await ParticipantsAsync(id, cancellationToken);
        return new AdminAppointmentDetails(row.Id, row.Vin, row.Make, row.Model, row.ProblemDescription, row.InspectionStartUtc,
            row.Status, row.CustomerName, row.CustomerPhone, history, row.Plan, participants, row.Version, payment, contacts, assignments);
    }
    private async Task<IReadOnlyList<AppointmentParticipant>> ParticipantsAsync(Guid id, CancellationToken cancellationToken)
    {
        // Use the most recent assignment snapshot once per employee, including inactive workers.
        var rows = await db.EmployeeAssignments.AsNoTracking().Where(a => a.AppointmentId == id)
            .OrderByDescending(a => a.AssignedAtUtc).ThenByDescending(a => a.Id)
            .Select(a => new { a.EmployeeId, a.NameSnapshot, a.SpecializationSnapshot }).ToListAsync(cancellationToken);
        return rows.DistinctBy(a => a.EmployeeId).Select(a => new AppointmentParticipant(a.NameSnapshot, a.SpecializationSnapshot)).ToArray();
    }
}
