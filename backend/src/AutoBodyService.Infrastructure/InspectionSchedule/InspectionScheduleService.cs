using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.InspectionSchedule;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.InspectionSchedule;

public sealed class InspectionScheduleService(ApplicationDbContext db) : IInspectionScheduleService
{
    public async Task<WeeklySchedule> GetAsync(CancellationToken cancellationToken)
    {
        var schedule = await db.InspectionSchedules.AsNoTracking().Include(s => s.Hours).SingleAsync(cancellationToken);
        return new WeeklySchedule(schedule.Version, schedule.Hours.OrderBy(h => h.DayOfWeek).ThenBy(h => h.Start)
            .Select(h => new WeeklyInterval(h.DayOfWeek, h.Start, h.End)).ToArray());
    }

    public async Task<ScheduleUpdateResult> UpdateAsync(Guid version, IReadOnlyList<WeeklyInterval> intervals, CancellationToken cancellationToken)
    {
        if (version == Guid.Empty || intervals.Any(i => !Enum.IsDefined(i.DayOfWeek) || i.Start >= i.End ||
                i.Start.Ticks % TimeSpan.TicksPerMinute != 0 || i.End.Ticks % TimeSpan.TicksPerMinute != 0 ||
                (i.End - i.Start).Ticks % TimeSpan.FromMinutes(30).Ticks != 0))
            return ScheduleUpdateResult.Invalid;

        foreach (var day in intervals.GroupBy(i => i.DayOfWeek))
        {
            var ordered = day.OrderBy(i => i.Start).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                if (ordered[i].Start < ordered[i - 1].End) return ScheduleUpdateResult.Invalid;
        }

        var schedule = await db.InspectionSchedules.Include(s => s.Hours).SingleAsync(cancellationToken);
        if (schedule.Version != version) return ScheduleUpdateResult.Conflict;
        db.InspectionWeeklyHours.RemoveRange(schedule.Hours);
        schedule.Hours = intervals.Select(i => new InspectionWeeklyHours
        {
            Id = Guid.NewGuid(), ScheduleId = schedule.Id, DayOfWeek = i.DayOfWeek, Start = i.Start, End = i.End
        }).ToList();
        db.InspectionWeeklyHours.AddRange(schedule.Hours);
        schedule.Version = Guid.NewGuid();
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return ScheduleUpdateResult.Conflict; }
        return ScheduleUpdateResult.Success;
    }
}
