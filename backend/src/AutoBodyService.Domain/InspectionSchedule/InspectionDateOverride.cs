namespace AutoBodyService.Domain.InspectionSchedule;

public enum DateOverrideMode { Weekly, Closed, Replace }
public enum SlotOverrideMode { Closed, Open }

public sealed class InspectionDateOverride
{
    public Guid Id { get; set; }
    public DateOnly Date { get; set; }
    public DateOverrideMode Mode { get; set; }
    public string? Reason { get; set; }
    public List<InspectionDateInterval> Intervals { get; set; } = [];
    public List<InspectionSlotOverride> Slots { get; set; } = [];
}

public sealed class InspectionDateInterval
{
    public Guid Id { get; set; }
    public Guid OverrideId { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
}

public sealed class InspectionSlotOverride
{
    public Guid Id { get; set; }
    public Guid OverrideId { get; set; }
    public TimeOnly Start { get; set; }
    public SlotOverrideMode Mode { get; set; }
}
