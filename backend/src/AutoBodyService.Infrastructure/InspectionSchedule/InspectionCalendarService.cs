using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoBodyService.Infrastructure.InspectionSchedule;

public sealed class InspectionCalendarService(ApplicationDbContext db, IInspectionScheduleService weekly,
    IInspectionExceptionsService exceptions, TimeProvider clock) : IInspectionCalendarService
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public async Task<IReadOnlyList<CalendarAppointment>> GetDayAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var from = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), Zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), Zone);
        return await Appointments(from, to, false).ToListAsync(cancellationToken);
    }

    public Task<ScheduleChangeResult> ChangeWeeklyAsync(Guid version, IReadOnlyList<WeeklyInterval> intervals, bool preview, string? confirmationToken, CancellationToken cancellationToken) =>
        ChangeAsync(version, null, new { kind = "weekly", intervals }, preview, confirmationToken,
            () => weekly.UpdateAsync(version, intervals, cancellationToken), cancellationToken);

    public Task<ScheduleChangeResult> ChangeDateAsync(Guid version, DateOnly date, InspectionException? exception, bool preview, string? confirmationToken, CancellationToken cancellationToken) =>
        ChangeAsync(version, date, new { kind = "date", date, exception }, preview, confirmationToken,
            () => exceptions.UpdateAsync(version, exception, date, cancellationToken), cancellationToken);

    private async Task<ScheduleChangeResult> ChangeAsync(Guid version, DateOnly? date, object proposal, bool preview, string? confirmationToken,
        Func<Task<ScheduleUpdateResult>> update, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Booking creation takes this same lock. Nothing can book against a stale preview while applying.
        await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"InspectionSchedules\" WHERE \"Id\" = 1 FOR UPDATE", cancellationToken);
        var result = await update();
        if (result != ScheduleUpdateResult.Success) return new(result);
        var now = clock.GetUtcNow();
        var rows = await Appointments(now, null, true).ToListAsync(cancellationToken);
        var conflicts = new List<CalendarAppointment>();
        foreach (var group in rows.GroupBy(a => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(a.InspectionStartUtc, Zone).DateTime)))
        {
            if (date.HasValue && date != group.Key) continue;
            var template = await exceptions.GetAsync(group.Key, cancellationToken);
            var starts = template.TemplateSlots.Select(slot => slot.StartUtc).ToHashSet();
            conflicts.AddRange(group.Where(appointment => !starts.Contains(appointment.InspectionStartUtc)));
        }
        // Bind the preview to the proposal, original version, and the exact affected records.
        var data = JsonSerializer.Serialize(new { version, proposal, conflicts });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        var response = new SchedulePreview(fingerprint, conflicts);
        if (preview) { await transaction.RollbackAsync(cancellationToken); return new(ScheduleUpdateResult.Success, response); }
        if ((confirmationToken is not null && confirmationToken != fingerprint) || (conflicts.Count > 0 && confirmationToken != fingerprint))
            return new(ScheduleUpdateResult.Conflict, response);
        await transaction.CommitAsync(cancellationToken);
        return new(ScheduleUpdateResult.Success, response);
    }

    private IQueryable<CalendarAppointment> Appointments(DateTimeOffset from, DateTimeOffset? to, bool onlyActive)
    {
        var appointments = db.Appointments.AsNoTracking().Where(a => a.InspectionStartUtc >= from);
        if (to.HasValue) appointments = appointments.Where(a => a.InspectionStartUtc < to.Value);
        if (onlyActive) appointments = appointments.Where(a => a.InspectionStartUtc > from && a.Status != AppointmentStatus.Cancelled);
        return from a in appointments
               join customer in db.Users.AsNoTracking() on a.UserId equals customer.Id
               orderby a.InspectionStartUtc, a.Id
               select new CalendarAppointment(a.Id, a.Make, a.Model, customer.Name, a.InspectionStartUtc, a.Status);
    }
}
