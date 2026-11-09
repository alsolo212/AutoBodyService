using AutoBodyService.Domain.InspectionSchedule;

namespace AutoBodyService.Application.InspectionSchedule;

public sealed record DateInterval(TimeOnly Start, TimeOnly End);
public sealed record SlotOverride(TimeOnly Start, SlotOverrideMode Mode);
public sealed record InspectionException(DateOnly Date, DateOverrideMode Mode, string? Reason, IReadOnlyList<DateInterval> Intervals, IReadOnlyList<SlotOverride> Slots);
public sealed record EffectiveSlot(TimeOnly Start, DateTimeOffset StartUtc);
public sealed record ExceptionSchedule(Guid Version, DateOnly Date, InspectionException? Exception, IReadOnlyList<EffectiveSlot> TemplateSlots);

public interface IInspectionExceptionsService
{
    Task<ExceptionSchedule> GetAsync(DateOnly date, CancellationToken cancellationToken);
    Task<ScheduleUpdateResult> UpdateAsync(Guid version, InspectionException? exception, DateOnly date, CancellationToken cancellationToken);
}
