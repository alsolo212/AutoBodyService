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

    public void RecordPriceItemChange(string actorId, Guid priceItemId, bool created)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(priceItemId, Guid.Empty);
        Add(actorId, created ? "PriceItemCreated" : "PriceItemChanged", "PriceListItem", priceItemId.ToString("D"), new { });
    }

    public void RecordManualContact(string actorId, Guid appointmentId, ManualContactReason reason)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(appointmentId, Guid.Empty);
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        Add(actorId, "ManualContactRecorded", "Appointment", appointmentId.ToString("D"), new { reason = reason.ToString() });
    }

    public void RecordAccountAction(string actorId, string targetId, string action, bool? value)
    {
        if (action is not ("AccountPasswordReset" or "AccountAdminChanged" or "AccountBlockedChanged"))
            throw new ArgumentOutOfRangeException(nameof(action));
        Add(actorId, action, "ApplicationUser", targetId, new { value });
    }

    public void RecordExpenseChange(string actorId, Guid expenseId, decimal? beforeAmount, decimal afterAmount,
        bool beforeHasDescription, bool afterHasDescription, bool descriptionChanged)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(expenseId, Guid.Empty);
        if (afterAmount <= 0 || beforeAmount <= 0) throw new ArgumentOutOfRangeException(nameof(afterAmount));
        // Free text can contain private data; retain safe before/after metadata only.
        Add(actorId, beforeAmount is null ? "ExpenseCreated" : "ExpenseCorrected", "MonthlyExpense", expenseId.ToString("D"),
            new { before = beforeAmount is null ? null : new { amount = beforeAmount, hasDescription = beforeHasDescription },
                after = new { amount = afterAmount, hasDescription = afterHasDescription }, descriptionChanged });
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
