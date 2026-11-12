using System.Text.Json;
using AutoBodyService.Application.Audit;
using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Infrastructure.Persistence;
using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Infrastructure.Audit;

public sealed class AdminAuditWriter(ApplicationDbContext db, TimeProvider clock) : IAdminAuditWriter
{
    public void RecordWeeklyScheduleChange(string actorId, IReadOnlyList<WeeklyInterval> before, IReadOnlyList<WeeklyInterval> after) =>
        Add(actorId, "InspectionScheduleChanged", "InspectionSchedule", "1", new { before, after });

    public void RecordDateExceptionChange(string actorId, DateOnly date, InspectionException? before, InspectionException? after) =>
        Add(actorId, "InspectionExceptionChanged", "InspectionDate", date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            new { before = SafeException(before), after = SafeException(after) });

    public void RecordAppointmentStatusChange(string actorId, Guid appointmentId, AppointmentStatus from, AppointmentStatus to)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to)) throw new ArgumentOutOfRangeException(nameof(to));
        Add(actorId, "AppointmentStatusChanged", "Appointment", appointmentId.ToString("D"), new { from = from.ToString(), to = to.ToString() });
    }

    public void RecordRepairPlanChange(string actorId, Guid appointmentId, bool priceChanged, bool datesChanged)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        if (!priceChanged && !datesChanged) throw new ArgumentException("At least one plan value must change.");
        Add(actorId, "RepairPlanChanged", "Appointment", appointmentId.ToString("D"), new { priceChanged, datesChanged });
    }

    // Reasons are free text and may contain private data; never copy them into audit details.
    private static object? SafeException(InspectionException? value) => value is null ? null : new
    {
        mode = value.Mode.ToString(),
        intervals = value.Intervals.Select(i => new { i.Start, i.End }).ToArray(),
        slots = value.Slots.Select(s => new { s.Start, mode = s.Mode.ToString() }).ToArray()
    };

    private void Add(string actorId, string action, string objectType, string objectId, object details)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Administrative audit requires the current business transaction.");
        db.AdminAuditEvents.Add(new AdminAuditEvent(actorId, action, objectType, objectId, clock.GetUtcNow(), JsonSerializer.Serialize(details)));
    }
}
