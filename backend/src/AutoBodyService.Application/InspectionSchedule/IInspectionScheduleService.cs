namespace AutoBodyService.Application.InspectionSchedule;

public sealed record WeeklyInterval(DayOfWeek DayOfWeek, TimeOnly Start, TimeOnly End);
public sealed record WeeklySchedule(Guid Version, IReadOnlyList<WeeklyInterval> Intervals);
public enum ScheduleUpdateResult { Success, Invalid, Conflict }

public interface IInspectionScheduleService
{
    Task<WeeklySchedule> GetAsync(CancellationToken cancellationToken);
    Task<ScheduleUpdateResult> UpdateAsync(Guid version, IReadOnlyList<WeeklyInterval> intervals, CancellationToken cancellationToken);
}
