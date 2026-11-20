using AutoBodyService.Application.Appointments;
using AutoBodyService.Application.Audit;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class RepairCapacityRecord
{
    public int Id { get; set; }
    public int? Workplaces { get; set; }
}

public sealed class RepairCapacityStore(ApplicationDbContext db, IAdminAuditWriter audit, TimeProvider clock) : IRepairCapacity
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    public static DateOnly LocalDate(DateTimeOffset value) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, Kyiv).DateTime);
    public static DateTimeOffset MidnightUtc(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), Kyiv), TimeSpan.Zero);

    internal static Task LockAsync(ApplicationDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"RepairCapacity\" WHERE \"Id\" = 1 FOR UPDATE", ct);

    internal static async Task<List<RepairPeriod>> ReservationsAsync(ApplicationDbContext db, Guid? excluded, CancellationToken ct)
    {
        var plans = await db.Appointments.AsNoTracking().Where(a => a.Id != excluded &&
            (a.Status == AppointmentStatus.Scheduled || a.Status == AppointmentStatus.InProgress) && a.RepairPlan != null)
            .Select(a => new { a.RepairPlan!.PlannedStartUtc, a.RepairPlan.EstimatedFinishUtc }).ToListAsync(ct);
        return plans.Select(p => new RepairPeriod(LocalDate(p.PlannedStartUtc), LocalDate(p.EstimatedFinishUtc))).ToList();
    }

    public async Task<RepairCapacityOverview> OverviewAsync(DateOnly firstDay, CancellationToken cancellationToken)
    {
        var capacity = await db.RepairCapacity.AsNoTracking().SingleAsync(cancellationToken);
        var actual = await db.Appointments.CountAsync(a => a.Status == AppointmentStatus.InProgress, cancellationToken);
        var plans = await ReservationsAsync(db, null, cancellationToken);
        var first = new DateOnly(firstDay.Year, firstDay.Month, 1);
        var days = Enumerable.Range(0, DateTime.DaysInMonth(first.Year, first.Month)).Select(i => first.AddDays(i))
            .Select(day => new RepairCapacityDay(day, plans.Count(p => p.Start <= day && day <= p.Finish))).ToArray();
        return new(capacity.Workplaces, actual, days);
    }

    public async Task<bool> SetWorkplacesAsync(int workplaces, string actorId, CancellationToken cancellationToken)
    {
        if (workplaces <= 0) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, cancellationToken);
        var item = await db.RepairCapacity.SingleAsync(cancellationToken);
        if (item.Workplaces != workplaces)
        {
            item.Workplaces = workplaces;
            audit.RecordRepairCapacityChange(actorId);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<RepairAvailabilityPreview?> PreviewAsync(DateOnly start, int durationDays, CancellationToken cancellationToken)
    {
        var today = LocalDate(clock.GetUtcNow());
        var latest = today.AddYears(1);
        if (start < today || start > latest || durationDays <= 0 || (long)start.DayNumber + durationDays > DateOnly.MaxValue.DayNumber) return null;
        var capacity = await db.RepairCapacity.AsNoTracking().SingleAsync(cancellationToken);
        var plans = await ReservationsAsync(db, null, cancellationToken);
        var actual = await db.Appointments.CountAsync(a => a.Status == AppointmentStatus.InProgress, cancellationToken);
        var end = start.AddDays(durationDays);
        return new(today, latest, capacity.Workplaces is int n ? RepairAvailability.Nearest(today, latest, durationDays, plans, n) : null,
            end, capacity.Workplaces is int count && RepairAvailability.Fits(new(start, end), plans, count), capacity.Workplaces, actual);
    }
}
