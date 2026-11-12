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
        return row is null ? null : new CustomerAppointmentDetails(row.Id, row.Vin, row.Make, row.Model, row.ProblemDescription,
            row.InspectionStartUtc, row.Status, row.History, row.Plan, null);
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
        return new AdminAppointmentDetails(row.Id, row.Vin, row.Make, row.Model, row.ProblemDescription, row.InspectionStartUtc,
            row.Status, row.CustomerName, row.CustomerPhone, history, row.Plan, null, row.Version);
    }
}
