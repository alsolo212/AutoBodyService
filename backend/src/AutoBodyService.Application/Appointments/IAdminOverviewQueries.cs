using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Appointments;

public sealed record AdminAppointmentFilter(AppointmentStatus? Status, string? Search, DateOnly? From, DateOnly? To,
    string Sort, bool Descending, int Page, int PageSize);
public sealed record AdminAppointmentListItem(Guid Id, string CustomerName, string Vin, string Make, string Model,
    DateTimeOffset InspectionStartUtc, DateTimeOffset InspectionStartLocal, AppointmentStatus Status, AgreedRepairDetails? RepairPlan, uint Version);
public sealed record AdminAppointmentPage(IReadOnlyList<AdminAppointmentListItem> Items, int Page, int PageSize, int Total);
public sealed record AdminWorkingCounts(int AwaitingInspection, int Scheduled, int InProgress, int AwaitingPayment);
public sealed record AdminDashboardItem(Guid Id, string CustomerName, string Make, string Model, AppointmentStatus Status,
    DateTimeOffset InspectionStartUtc, DateTimeOffset InspectionStartLocal, DateTimeOffset OccurredAtUtc);
public sealed record AdminContactTask(Guid AppointmentId, string CustomerName, string Make, string Model, ManualContactReason Reason, DateTimeOffset? TriggeredAtUtc);
public sealed record AdminDashboard(AdminWorkingCounts Counts, IReadOnlyList<AdminDashboardItem> UpcomingInspections,
    IReadOnlyList<AdminDashboardItem> NewAppointments, IReadOnlyList<AdminDashboardItem> Cancellations, IReadOnlyList<AdminContactTask> ContactTasks);

public interface IAdminOverviewQueries
{
    Task<AdminAppointmentPage> GetAppointmentsAsync(AdminAppointmentFilter filter, CancellationToken cancellationToken);
    Task<AdminDashboard> GetDashboardAsync(CancellationToken cancellationToken);
}
public enum ManualContactResult { Recorded, NotFound, Invalid, Conflict }
public interface IManualContactService
{
    Task<ManualContactResult> RecordAsync(Guid appointmentId, ManualContactReason reason, string actorId, CancellationToken cancellationToken);
}
