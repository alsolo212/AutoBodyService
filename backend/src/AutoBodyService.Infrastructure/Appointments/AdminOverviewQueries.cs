using AutoBodyService.Application.Appointments;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class AdminOverviewQueries(ApplicationDbContext db, IInspectionCalendarService calendar, TimeProvider clock) : IAdminOverviewQueries
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    private static DateTimeOffset Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);
    private static DateTimeOffset MidnightUtc(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), Zone), TimeSpan.Zero);

    public async Task<AdminAppointmentPage> GetAppointmentsAsync(AdminAppointmentFilter filter, CancellationToken cancellationToken)
    {
        var query = from a in db.Appointments.AsNoTracking()
                    join user in db.Users.AsNoTracking() on a.UserId equals user.Id
                    select new { Appointment = a, Customer = user.Name, Phone = user.PhoneNormalized };
        if (filter.Status.HasValue) query = query.Where(x => x.Appointment.Status == filter.Status.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var escaped = filter.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var term = "%" + escaped + "%";
            query = query.Where(x => EF.Functions.ILike(x.Customer, term, "\\") || EF.Functions.ILike(x.Phone, term, "\\") ||
                EF.Functions.ILike(x.Appointment.Vin, term, "\\") || EF.Functions.ILike(x.Appointment.Make, term, "\\") || EF.Functions.ILike(x.Appointment.Model, term, "\\"));
        }
        if (filter.From.HasValue) { var from = MidnightUtc(filter.From.Value); query = query.Where(x => x.Appointment.InspectionStartUtc >= from); }
        if (filter.To.HasValue) { var to = MidnightUtc(filter.To.Value.AddDays(1)); query = query.Where(x => x.Appointment.InspectionStartUtc < to); }
        var total = await query.CountAsync(cancellationToken);
        query = (filter.Sort, filter.Descending) switch
        {
            ("created", false) => query.OrderBy(x => x.Appointment.CreatedAtUtc).ThenBy(x => x.Appointment.Id),
            ("created", true) => query.OrderByDescending(x => x.Appointment.CreatedAtUtc).ThenBy(x => x.Appointment.Id),
            ("customer", false) => query.OrderBy(x => x.Customer).ThenBy(x => x.Appointment.Id),
            ("customer", true) => query.OrderByDescending(x => x.Customer).ThenBy(x => x.Appointment.Id),
            (_, true) => query.OrderByDescending(x => x.Appointment.InspectionStartUtc).ThenBy(x => x.Appointment.Id),
            _ => query.OrderBy(x => x.Appointment.InspectionStartUtc).ThenBy(x => x.Appointment.Id)
        };
        var rows = await query.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(x => new { x.Appointment.Id, x.Customer, x.Appointment.Vin, x.Appointment.Make, x.Appointment.Model, x.Appointment.InspectionStartUtc,
                x.Appointment.Status, Version = EF.Property<uint>(x.Appointment, "Version"), Plan = x.Appointment.RepairPlan == null ? null : new AgreedRepairDetails(x.Appointment.RepairPlan.AgreedPrice,
                    x.Appointment.RepairPlan.Currency, x.Appointment.RepairPlan.PlannedStartUtc, x.Appointment.RepairPlan.EstimatedFinishUtc) }).ToListAsync(cancellationToken);
        return new(rows.Select(x => new AdminAppointmentListItem(x.Id, x.Customer, x.Vin, x.Make, x.Model, x.InspectionStartUtc,
            Local(x.InspectionStartUtc), x.Status, x.Plan, x.Version)).ToArray(), filter.Page, filter.PageSize, total);
    }

    public async Task<AdminDashboard> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var totals = await db.Appointments.AsNoTracking().GroupBy(a => a.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        int Count(AppointmentStatus status) => totals.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
        var counts = new AdminWorkingCounts(Count(AppointmentStatus.AwaitingInspection), Count(AppointmentStatus.Scheduled), Count(AppointmentStatus.InProgress), Count(AppointmentStatus.AwaitingPayment));
        var rows = from a in db.Appointments.AsNoTracking()
                   join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                   select new { Appointment = a, Customer = u.Name };
        var upcomingRows = await rows.Where(x => x.Appointment.Status == AppointmentStatus.AwaitingInspection && x.Appointment.InspectionStartUtc > now)
            .OrderBy(x => x.Appointment.InspectionStartUtc).ThenBy(x => x.Appointment.Id).Take(10)
            .Select(x => new { x.Appointment.Id, x.Customer, x.Appointment.Make, x.Appointment.Model, x.Appointment.Status, x.Appointment.InspectionStartUtc, x.Appointment.CreatedAtUtc }).ToListAsync(cancellationToken);
        var newRows = await rows.OrderByDescending(x => x.Appointment.CreatedAtUtc).ThenBy(x => x.Appointment.Id).Take(10)
            .Select(x => new { x.Appointment.Id, x.Customer, x.Appointment.Make, x.Appointment.Model, x.Appointment.Status, x.Appointment.InspectionStartUtc, x.Appointment.CreatedAtUtc }).ToListAsync(cancellationToken);
        var cancelledRows = await (from e in db.AppointmentStatusEvents.AsNoTracking()
                                   join a in db.Appointments.AsNoTracking() on e.AppointmentId equals a.Id
                                   join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                                   where e.ToStatus == AppointmentStatus.Cancelled
                                   orderby e.ChangedAtUtc descending, e.Id
                                   select new { a.Id, Customer = u.Name, a.Make, a.Model, a.Status, a.InspectionStartUtc, Occurred = e.ChangedAtUtc }).Take(10).ToListAsync(cancellationToken);
        var upcoming = upcomingRows.Select(x => new AdminDashboardItem(x.Id, x.Customer, x.Make, x.Model, x.Status, x.InspectionStartUtc, Local(x.InspectionStartUtc), x.InspectionStartUtc)).ToArray();
        var recent = newRows.Select(x => new AdminDashboardItem(x.Id, x.Customer, x.Make, x.Model, x.Status, x.InspectionStartUtc, Local(x.InspectionStartUtc), x.CreatedAtUtc)).ToArray();
        var cancellations = cancelledRows.Select(x => new AdminDashboardItem(x.Id, x.Customer, x.Make, x.Model, x.Status, x.InspectionStartUtc, Local(x.InspectionStartUtc), x.Occurred)).ToArray();

        // Pending tasks are derived suggestions. Only explicit ManualContactEvent records mark a purpose as contacted.
        var candidates = from x in rows
                         where x.Appointment.Status == AppointmentStatus.AwaitingInspection || x.Appointment.Status == AppointmentStatus.Scheduled || x.Appointment.Status == AppointmentStatus.AwaitingPayment
                         let reason = x.Appointment.Status == AppointmentStatus.AwaitingInspection ? ManualContactReason.NewAppointment :
                             x.Appointment.Status == AppointmentStatus.Scheduled ? ManualContactReason.Agreement : ManualContactReason.ReadyForPayment
                         let transition = db.AppointmentStatusEvents.Where(e => e.AppointmentId == x.Appointment.Id && e.ToStatus == x.Appointment.Status)
                             .OrderByDescending(e => e.ChangedAtUtc).Select(e => (DateTimeOffset?)e.ChangedAtUtc).FirstOrDefault()
                         let trigger = x.Appointment.Status == AppointmentStatus.AwaitingInspection ? (DateTimeOffset?)x.Appointment.CreatedAtUtc :
                             x.Appointment.Status == AppointmentStatus.Scheduled && x.Appointment.RepairPlan != null && (transition == null || x.Appointment.RepairPlan.UpdatedAtUtc > transition)
                             ? x.Appointment.RepairPlan.UpdatedAtUtc : transition
                         where !db.ManualContactEvents.Any(c => c.AppointmentId == x.Appointment.Id && c.Reason == reason && (trigger == null || c.RecordedAtUtc >= trigger))
                         orderby trigger == null, trigger descending, x.Appointment.Id
                         select new { x.Appointment.Id, x.Customer, x.Appointment.Make, x.Appointment.Model, Reason = reason, Trigger = trigger };
        var tasks = new List<AdminContactTask>();
        foreach (var reason in new[] { ManualContactReason.NewAppointment, ManualContactReason.Agreement, ManualContactReason.ReadyForPayment })
        {
            var pending = await candidates.Where(t => t.Reason == reason).Take(20).ToListAsync(cancellationToken);
            tasks.AddRange(pending.Select(t => new AdminContactTask(t.Id, t.Customer, t.Make, t.Model, t.Reason, t.Trigger)));
        }
        var conflicts = await calendar.GetCurrentConflictsAsync(cancellationToken);
        if (conflicts.Count > 0)
        {
            var ids = conflicts.Select(c => c.Id).ToArray();
            var created = await db.Appointments.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.CreatedAtUtc }).ToDictionaryAsync(x => x.Id, x => x.CreatedAtUtc, cancellationToken);
            var contacts = await db.ManualContactEvents.AsNoTracking().Where(c => ids.Contains(c.AppointmentId) && c.Reason == ManualContactReason.ScheduleConflictOrReschedule)
                .GroupBy(c => c.AppointmentId).Select(g => new { Id = g.Key, Last = g.Max(c => c.RecordedAtUtc) }).ToDictionaryAsync(x => x.Id, x => x.Last, cancellationToken);
            var dateKeys = conflicts.Select(c => Local(c.InspectionStartUtc).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToArray();
            var weekly = await db.AdminAuditEvents.AsNoTracking().Where(e => e.Action == "InspectionScheduleChanged")
                .MaxAsync(e => (DateTimeOffset?)e.OccurredAtUtc, cancellationToken);
            var dateChanges = await db.AdminAuditEvents.AsNoTracking().Where(e => e.Action == "InspectionExceptionChanged" && dateKeys.Contains(e.ObjectId))
                .GroupBy(e => e.ObjectId).Select(g => new { Date = g.Key, Last = g.Max(e => e.OccurredAtUtc) }).ToListAsync(cancellationToken);
            var byDate = dateChanges.ToDictionary(e => e.Date, e => e.Last);
            foreach (var c in conflicts)
            {
                DateTimeOffset? trigger = weekly > created[c.Id] ? weekly : null;
                var date = Local(c.InspectionStartUtc).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                if (byDate.TryGetValue(date, out var changed) && changed > created[c.Id] && (trigger is null || changed > trigger)) trigger = changed;
                if (!contacts.TryGetValue(c.Id, out var contact) || (trigger.HasValue && contact < trigger))
                    tasks.Add(new AdminContactTask(c.Id, c.CustomerName, c.Make, c.Model, ManualContactReason.ScheduleConflictOrReschedule, trigger));
            }
        }
        // Round-robin keeps each reason visible when another reason has a large backlog.
        var queues = tasks.GroupBy(t => t.Reason).OrderBy(g => g.Key).Select(g => new Queue<AdminContactTask>(g)).ToArray();
        var selected = new List<AdminContactTask>();
        while (selected.Count < 20 && queues.Any(q => q.Count != 0))
            foreach (var queue in queues)
                if (queue.Count != 0 && selected.Count < 20) selected.Add(queue.Dequeue());
        return new AdminDashboard(counts, upcoming, recent, cancellations, selected);
    }
}
