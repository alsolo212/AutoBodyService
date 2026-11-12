using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Appointments;

public sealed class AdminAppointmentCommands(IAdminAppointmentStore store) : IAdminAppointmentCommands
{
    public Task<AdminAppointmentResult> PutPlanAsync(Guid id, uint version, string actorId, RepairPlanCommand command, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var plan = new RepairPlan(id, command.Price, command.PlannedStartUtc, command.EstimatedFinishUtc, now);
            var previous = appointment.RepairPlan;
            var priceChanged = previous is null || previous.AgreedPrice != plan.AgreedPrice;
            var datesChanged = previous is null || previous.PlannedStartUtc != plan.PlannedStartUtc || previous.EstimatedFinishUtc != plan.EstimatedFinishUtc;
            if (!priceChanged && !datesChanged)
            {
                if (appointment.Status is not (AppointmentStatus.AwaitingInspection or AppointmentStatus.Scheduled or AppointmentStatus.InProgress or AppointmentStatus.AwaitingPayment)
                    || (appointment.Status == AppointmentStatus.AwaitingInspection && !command.InspectionPerformed))
                    return new(AdminAppointmentResult.Conflict);
                return new(AdminAppointmentResult.Success);
            }
            if (appointment.Status == AppointmentStatus.AwaitingInspection) appointment.AgreeRepairPlan(plan, command.InspectionPerformed);
            else appointment.ReviseRepairPlan(plan.AgreedPrice, plan.PlannedStartUtc, plan.EstimatedFinishUtc, now);
            return new(AdminAppointmentResult.Success, PriceChanged: priceChanged, DatesChanged: datesChanged);
        }), cancellationToken);

    public Task<AdminAppointmentResult> TransitionAsync(Guid id, uint version, string actorId, TransitionCommand command, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var from = appointment.Status;
            PaymentHandoverConfirmation? confirmation = null;
            switch (command.Target)
            {
                case AppointmentStatus.Scheduled:
                    if (appointment.RepairPlan is null) return new(AdminAppointmentResult.Conflict);
                    appointment.Schedule(appointment.RepairPlan, command.InspectionPerformed);
                    break;
                case AppointmentStatus.InProgress: appointment.StartRepair(); break;
                case AppointmentStatus.AwaitingPayment: appointment.FinishRepair(); break;
                case AppointmentStatus.Completed:
                    appointment.Complete(command.PaymentReceived, command.VehicleHandedOver);
                    confirmation = new PaymentHandoverConfirmation(id, actorId, now);
                    break;
                default: return new(AdminAppointmentResult.Conflict);
            }
            var history = AppointmentStatusEvent.ForTransition(id, from, appointment.Status, actorId, now);
            // When assignments are introduced, close them here in the same transaction on exit from InProgress.
            return new(AdminAppointmentResult.Success, history, confirmation);
        }), cancellationToken);

    public Task<AdminAppointmentResult> CancelAsync(Guid id, uint version, string actorId, bool confirmed, string reason, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var from = appointment.Status;
            appointment.CancelByAdmin(confirmed, reason);
            var history = AppointmentStatusEvent.ForTransition(id, from, appointment.Status, actorId, now, reason);
            // Assignment closure will join this same transaction when that model exists.
            return new(AdminAppointmentResult.Success, history);
        }), cancellationToken);

    private static AdminAppointmentMutation Guard(Func<AdminAppointmentMutation> action)
    {
        try { return action(); }
        catch (ArgumentException) { return new(AdminAppointmentResult.Invalid); }
        catch (InvalidOperationException) { return new(AdminAppointmentResult.Conflict); }
    }
}
