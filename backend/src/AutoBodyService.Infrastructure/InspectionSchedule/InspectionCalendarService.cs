using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Application.Audit;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Persistence;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoBodyService.Infrastructure.InspectionSchedule;

public sealed class InspectionCalendarService(ApplicationDbContext db, IInspectionScheduleService weekly,
    IInspectionExceptionsService exceptions, IAdminAuditWriter audit, TimeProvider clock, INotificationRealtimePublisher? publisher = null) : IInspectionCalendarService
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public async Task<IReadOnlyList<CalendarAppointment>> GetDayAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var from = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), Zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), Zone);
        return await Appointments(from, to, false).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarAppointment>> GetCurrentConflictsAsync(CancellationToken cancellationToken)
    {
        var rows = await Appointments(clock.GetUtcNow(), null, true).ToListAsync(cancellationToken);
        return await FindConflictsAsync(rows, null, cancellationToken);
    }

    public Task<ScheduleChangeResult> ChangeWeeklyAsync(string actorId, Guid version, IReadOnlyList<WeeklyInterval> intervals, bool preview, string? confirmationToken, CancellationToken cancellationToken) =>
        ChangeAsync(version, null, new { kind = "weekly", intervals }, preview, confirmationToken,
            async () => (await weekly.GetAsync(cancellationToken)).Intervals,
            () => weekly.UpdateAsync(version, intervals, cancellationToken),
            before => audit.RecordWeeklyScheduleChange(actorId, before, intervals), cancellationToken);

    public Task<ScheduleChangeResult> ChangeDateAsync(string actorId, Guid version, DateOnly date, InspectionException? exception, bool preview, string? confirmationToken, CancellationToken cancellationToken) =>
        ChangeAsync(version, date, new { kind = "date", date, exception }, preview, confirmationToken,
            async () => (await exceptions.GetAsync(date, cancellationToken)).Exception,
            () => exceptions.UpdateAsync(version, exception, date, cancellationToken),
            before => audit.RecordDateExceptionChange(actorId, date, before, exception), cancellationToken);

    private async Task<ScheduleChangeResult> ChangeAsync<T>(Guid version, DateOnly? date, object proposal, bool preview, string? confirmationToken,
        Func<Task<T>> readBefore, Func<Task<ScheduleUpdateResult>> update, Action<T> recordAudit, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Booking creation takes this same lock. Nothing can book against a stale preview while applying.
        await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"InspectionSchedules\" WHERE \"Id\" = 1 FOR UPDATE", cancellationToken);
        var before = await readBefore();
        var now = clock.GetUtcNow();
        var rowsBefore = await Appointments(now, null, true).ToListAsync(cancellationToken);
        var previousConflicts = (await FindConflictsAsync(rowsBefore, date, cancellationToken)).Select(x => x.Id).ToHashSet();
        var result = await update();
        if (result != ScheduleUpdateResult.Success) return new(result);
        var rows = await Appointments(now, null, true).ToListAsync(cancellationToken);
        var conflicts = await FindConflictsAsync(rows, date, cancellationToken);
        // Bind the preview to the proposal, original version, and the exact affected records.
        var data = JsonSerializer.Serialize(new { version, proposal, conflicts });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        var response = new SchedulePreview(fingerprint, conflicts);
        if (preview) { await transaction.RollbackAsync(cancellationToken); return new(ScheduleUpdateResult.Success, response); }
        if ((confirmationToken is not null && confirmationToken != fingerprint) || (conflicts.Count > 0 && confirmationToken != fingerprint))
            return new(ScheduleUpdateResult.Conflict, response);
        recordAudit(before);
        foreach (var conflict in conflicts.Where(c => !previousConflicts.Contains(c.Id) && c.Status is not (AppointmentStatus.Cancelled or AppointmentStatus.Completed)))
            await NotificationEvents.AddForAdministratorsAsync(db, conflict.Id, "AdminCalendarConflict", "Конфлікт розкладу",
                "Зміна графіка вплинула на запис. Зв’яжіться з клієнтом вручну.", now,
                $"schedule:{version}:{conflict.Id}", cancellationToken);
        var recipients = NotificationDispatch.PendingRecipients(db);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await NotificationDispatch.AfterCommitAsync(publisher, recipients);
        return new(ScheduleUpdateResult.Success, response);
    }

    private async Task<List<CalendarAppointment>> FindConflictsAsync(IReadOnlyList<CalendarAppointment> rows, DateOnly? date, CancellationToken cancellationToken)
    {
        var conflicts = new List<CalendarAppointment>();
        foreach (var group in rows.GroupBy(a => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(a.InspectionStartUtc, Zone).DateTime)))
        {
            if (date.HasValue && date != group.Key) continue;
            var template = await exceptions.GetAsync(group.Key, cancellationToken);
            var starts = template.TemplateSlots.Select(slot => slot.StartUtc).ToHashSet();
            conflicts.AddRange(group.Where(appointment => !starts.Contains(appointment.InspectionStartUtc)));
        }
        return conflicts;
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
