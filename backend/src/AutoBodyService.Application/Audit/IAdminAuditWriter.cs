using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Audit;

/// <summary>Stages safe administrative events in the current business transaction.</summary>
public interface IAdminAuditWriter
{
    void RecordWeeklyScheduleChange(string actorId, IReadOnlyList<WeeklyInterval> before, IReadOnlyList<WeeklyInterval> after);
    void RecordDateExceptionChange(string actorId, DateOnly date, InspectionException? before, InspectionException? after);
    void RecordAppointmentStatusChange(string actorId, Guid appointmentId, AppointmentStatus from, AppointmentStatus to);
    void RecordRepairPlanChange(string actorId, Guid appointmentId, bool priceChanged, bool datesChanged);
}
