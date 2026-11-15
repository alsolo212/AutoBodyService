using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Audit;

/// <summary>Stages safe administrative events in the current business transaction.</summary>
public interface IAdminAuditWriter
{
    void RecordWeeklyScheduleChange(string actorId, IReadOnlyList<WeeklyInterval> before, IReadOnlyList<WeeklyInterval> after);
    void RecordDateExceptionChange(string actorId, DateOnly date, InspectionException? before, InspectionException? after);
    void RecordAppointmentStatusChange(string actorId, Guid appointmentId, AppointmentStatus from, AppointmentStatus to, Guid? statusEventId = null);
    void RecordAssignmentChange(string actorId, Guid assignmentId, Guid appointmentId, Guid employeeId, bool released, bool automatic);
    Task<AuditPage> ReadAsync(AuditQuery query, CancellationToken cancellationToken);
    void RecordRepairPlanChange(string actorId, Guid appointmentId, bool priceChanged, bool datesChanged);
    void RecordPriceItemChange(string actorId, Guid priceItemId, bool created);
    void RecordManualContact(string actorId, Guid appointmentId, ManualContactReason reason);
    void RecordAccountAction(string actorId, string targetId, string action, bool? value);
    void RecordExpenseChange(string actorId, Guid expenseId, decimal? beforeAmount, decimal afterAmount,
        bool beforeHasDescription, bool afterHasDescription, bool descriptionChanged);
}
