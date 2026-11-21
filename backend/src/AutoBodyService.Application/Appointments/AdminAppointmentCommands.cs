using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Appointments;

public sealed class AdminAppointmentCommands(IAdminAppointmentStore store) : IAdminAppointmentCommands
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public Task<AdminAppointmentResult> CorrectStatusAsync(Guid id, uint version, string actorId, AppointmentStatus target,
        string reason, bool confirmed, DateTimeOffset? inspectionStartUtc, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            if (!confirmed) return new(AdminAppointmentResult.Invalid);
            var previous = appointment.Status; var oldSlot = appointment.InspectionStartUtc;
            appointment.CorrectStatus(target, inspectionStartUtc);
            return new(AdminAppointmentResult.Success, AppointmentStatusEvent.ForCorrection(id, previous, target, actorId, now, reason,
                previous == AppointmentStatus.Cancelled ? oldSlot : null,
                previous == AppointmentStatus.Cancelled ? appointment.InspectionStartUtc : null));
        }), cancellationToken);

    public Task<AdminAppointmentResult> PauseAsync(Guid id, uint version, string actorId, PauseRepairCommand command, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Kyiv).DateTime);
            var pause = new RepairPause(id, command.Reason, command.DurationDays, command.WorkplaceReleased, today, actorId, now);
            var from = appointment.Status; appointment.PauseRepair();
            return new(AdminAppointmentResult.Success, AppointmentStatusEvent.ForTransition(id, from, appointment.Status, actorId, now), Pause: pause);
        }), cancellationToken);

    public Task<AdminAppointmentResult> ResumeAsync(Guid id, uint version, string actorId, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var from = appointment.Status; appointment.ResumeRepair();
            return new(AdminAppointmentResult.Success, AppointmentStatusEvent.ForTransition(id, from, appointment.Status, actorId, now));
        }), cancellationToken);

    public Task<AdminAppointmentResult> DelayAsync(Guid id, uint version, string actorId, DateOnly finish, string confirmationToken, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var plan = appointment.RepairPlan;
            if (plan is null || appointment.Status is not (AppointmentStatus.InProgress or AppointmentStatus.Paused) ||
                finish <= DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(plan.EstimatedFinishUtc, Kyiv).DateTime) || confirmationToken.Length != 64)
                return new(AdminAppointmentResult.Invalid);
            var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(finish.ToDateTime(TimeOnly.MinValue), Kyiv));
            if (plan.CalendarDatesOnly) appointment.ReviseCalendarRepairPlan(plan.AgreedPrice, plan.PlannedStartUtc, end, now);
            else appointment.ReviseRepairPlan(plan.AgreedPrice, plan.PlannedStartUtc, end, now);
            return new(AdminAppointmentResult.Success, DatesChanged: true, DelayToken: confirmationToken);
        }), cancellationToken);

    public Task<AdminAppointmentResult> AcceptEarlierAsync(Guid id, uint version, string actorId, Guid? pauseId, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var plan = appointment.RepairPlan;
            if (appointment.Status != AppointmentStatus.Scheduled || plan is null) return new(AdminAppointmentResult.Conflict);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Kyiv).DateTime);
            var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(plan.PlannedStartUtc, Kyiv).DateTime);
            var end = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(plan.EstimatedFinishUtc, Kyiv).DateTime);
            var duration = end.DayNumber - start.DayNumber;
            if (start <= today || duration <= 0 || (long)today.DayNumber + duration > DateOnly.MaxValue.DayNumber) return new(AdminAppointmentResult.Conflict);
            var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), Kyiv));
            var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(today.AddDays(duration).ToDateTime(TimeOnly.MinValue), Kyiv));
            appointment.ReviseCalendarRepairPlan(plan.AgreedPrice, startUtc, endUtc, now);
            return new(AdminAppointmentResult.Success, DatesChanged: true, OfferPauseId: pauseId, EarlyOffer: pauseId is null);
        }), cancellationToken);

    public Task<AdminAppointmentResult> ScheduleAsync(Guid id, uint version, string actorId, ScheduleRepairCommand command, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Kyiv).DateTime);
            if (command.Start < today || command.Start > today.AddYears(1) || command.DurationDays <= 0 ||
                (long)command.Start.DayNumber + command.DurationDays > DateOnly.MaxValue.DayNumber || !command.InspectionPerformed)
                return new(AdminAppointmentResult.Invalid);
            var end = command.Start.AddDays(command.DurationDays);
            var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(command.Start.ToDateTime(TimeOnly.MinValue), Kyiv));
            var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(end.ToDateTime(TimeOnly.MinValue), Kyiv));
            var plan = new RepairPlan(id, command.Price, startUtc, endUtc, now, calendarDatesOnly: true);
            var priceChange = appointment.RepairPlan is { } old && old.AgreedPrice != command.Price
                ? new RepairPriceChange(id, old.AgreedPrice, command.Price, command.PriceReason ?? "", actorId, now) : null;
            var from = appointment.Status;
            appointment.AgreeRepairPlan(plan, true);
            appointment.Schedule(appointment.RepairPlan!, true);
            return new(AdminAppointmentResult.Success, AppointmentStatusEvent.ForTransition(id, from, appointment.Status, actorId, now), PriceChanged: true, DatesChanged: true, PriceChange: priceChange);
        }), cancellationToken);

    public Task<AdminAppointmentResult> PutPlanAsync(Guid id, uint version, string actorId, RepairPlanCommand command, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            if (appointment.Status == AppointmentStatus.AwaitingInspection) return new(AdminAppointmentResult.Conflict);
            var plan = new RepairPlan(id, command.Price, command.PlannedStartUtc, command.EstimatedFinishUtc, now);
            var previous = appointment.RepairPlan;
            var priceChanged = previous is null || previous.AgreedPrice != plan.AgreedPrice;
            var datesChanged = previous is null || previous.PlannedStartUtc != plan.PlannedStartUtc || previous.EstimatedFinishUtc != plan.EstimatedFinishUtc;
            var priceChange = priceChanged && previous is not null
                ? new RepairPriceChange(id, previous.AgreedPrice, plan.AgreedPrice, command.PriceReason ?? "", actorId, now) : null;
            if (appointment.Status == AppointmentStatus.Scheduled && datesChanged && !command.CustomerAgreed) return new(AdminAppointmentResult.Conflict);
            if (!priceChanged && !datesChanged)
            {
                if (appointment.Status is not (AppointmentStatus.AwaitingInspection or AppointmentStatus.Scheduled or AppointmentStatus.InProgress or AppointmentStatus.Paused or AppointmentStatus.AwaitingPayment)
                    || (appointment.Status == AppointmentStatus.AwaitingInspection && !command.InspectionPerformed))
                    return new(AdminAppointmentResult.Conflict);
                return new(AdminAppointmentResult.Success);
            }
            if (appointment.Status == AppointmentStatus.AwaitingInspection) appointment.AgreeRepairPlan(plan, command.InspectionPerformed);
            else appointment.ReviseRepairPlan(plan.AgreedPrice, plan.PlannedStartUtc, plan.EstimatedFinishUtc, now);
            return new(AdminAppointmentResult.Success, PriceChanged: priceChanged, DatesChanged: datesChanged, PriceChange: priceChange);
        }), cancellationToken);

    public Task<AdminAppointmentResult> TransitionAsync(Guid id, uint version, string actorId, TransitionCommand command, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var from = appointment.Status;
            PaymentHandoverConfirmation? confirmation = null;
            switch (command.Target)
            {
                case AppointmentStatus.Scheduled:
                    return new(AdminAppointmentResult.Conflict);
                case AppointmentStatus.InProgress: appointment.StartRepair(); break;
                case AppointmentStatus.AwaitingPayment: appointment.FinishRepair(); break;
                case AppointmentStatus.Completed:
                    appointment.Complete(command.PaymentReceived, command.VehicleHandedOver);
                    confirmation = new PaymentHandoverConfirmation(id, actorId, now);
                    break;
                default: return new(AdminAppointmentResult.Conflict);
            }
            var history = AppointmentStatusEvent.ForTransition(id, from, appointment.Status, actorId, now);
            return new(AdminAppointmentResult.Success, history, confirmation);
        }), cancellationToken);

    public Task<AdminAppointmentResult> CancelAsync(Guid id, uint version, string actorId, bool confirmed, string reason, CancellationToken cancellationToken) =>
        store.ExecuteAsync(id, version, actorId, (appointment, now) => Guard(() =>
        {
            var from = appointment.Status;
            appointment.CancelByAdmin(confirmed, reason);
            var history = AppointmentStatusEvent.ForTransition(id, from, appointment.Status, actorId, now, reason);
            return new(AdminAppointmentResult.Success, history);
        }), cancellationToken);

    private static AdminAppointmentMutation Guard(Func<AdminAppointmentMutation> action)
    {
        try { return action(); }
        catch (ArgumentException) { return new(AdminAppointmentResult.Invalid); }
        catch (InvalidOperationException) { return new(AdminAppointmentResult.Conflict); }
    }
}
