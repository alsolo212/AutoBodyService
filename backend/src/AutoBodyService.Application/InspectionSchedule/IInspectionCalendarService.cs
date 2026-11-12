using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.InspectionSchedule;

/// <summary>Minimal administrative information for an inspection on the calendar.</summary>
public sealed record CalendarAppointment(Guid Id, string Make, string Model, string CustomerName, DateTimeOffset InspectionStartUtc, AppointmentStatus Status);
/// <summary>The affected future inspections and confirmation token for a proposed schedule.</summary>
public sealed record SchedulePreview(string ConfirmationToken, IReadOnlyList<CalendarAppointment> Conflicts);
public sealed record ScheduleChangeResult(ScheduleUpdateResult Status, SchedulePreview? Preview = null);

public interface IInspectionCalendarService
{
    Task<IReadOnlyList<CalendarAppointment>> GetDayAsync(DateOnly date, CancellationToken cancellationToken);
    Task<ScheduleChangeResult> ChangeWeeklyAsync(string actorId, Guid version, IReadOnlyList<WeeklyInterval> intervals, bool preview, string? confirmationToken, CancellationToken cancellationToken);
    Task<ScheduleChangeResult> ChangeDateAsync(string actorId, Guid version, DateOnly date, InspectionException? exception, bool preview, string? confirmationToken, CancellationToken cancellationToken);
}
