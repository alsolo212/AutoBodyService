using AutoBodyService.Application.InspectionSchedule;

namespace AutoBodyService.IntegrationTests;

// Legacy in-memory controller tests exercise validation and versions; PostgreSQL tests exercise conflict handling.
public sealed class InMemoryScheduleTestAdapter(IInspectionScheduleService weekly, IInspectionExceptionsService exceptions) : IInspectionCalendarService
{
    public Task<IReadOnlyList<CalendarAppointment>> GetDayAsync(DateOnly date, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<CalendarAppointment>> GetCurrentConflictsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public async Task<ScheduleChangeResult> ChangeWeeklyAsync(string actorId, Guid version, IReadOnlyList<WeeklyInterval> intervals, bool preview, string? confirmationToken, CancellationToken cancellationToken) =>
        preview ? throw new NotSupportedException() : new(await weekly.UpdateAsync(version, intervals, cancellationToken), new SchedulePreview("", []));
    public async Task<ScheduleChangeResult> ChangeDateAsync(string actorId, Guid version, DateOnly date, InspectionException? exception, bool preview, string? confirmationToken, CancellationToken cancellationToken) =>
        preview ? throw new NotSupportedException() : new(await exceptions.UpdateAsync(version, exception, date, cancellationToken), new SchedulePreview("", []));
}
