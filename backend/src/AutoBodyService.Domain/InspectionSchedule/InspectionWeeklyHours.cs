namespace AutoBodyService.Domain.InspectionSchedule;

// Times are local wall-clock values in Europe/Kyiv, not UTC instants.
public sealed class InspectionWeeklyHours
{
    public Guid Id { get; set; }
    public int ScheduleId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
}

public sealed class InspectionSchedule
{
    public int Id { get; set; }
    public Guid Version { get; set; }
    public List<InspectionWeeklyHours> Hours { get; set; } = [];
}
