using System.Reflection;
using System.Text.Json;
using AutoBodyService.Api.Controllers;
using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.IntegrationTests;

public sealed class AppointmentLifecycleDomainTests
{
    private static readonly DateTimeOffset Inspection = new(2045, 1, 2, 10, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> TransitionCases()
    {
        foreach (var from in Enum.GetValues<AppointmentStatus>())
        foreach (var to in Enum.GetValues<AppointmentStatus>())
        {
            var allowed = (from, to) is
                (AppointmentStatus.AwaitingInspection, AppointmentStatus.Scheduled) or
                (AppointmentStatus.Scheduled, AppointmentStatus.InProgress) or
                (AppointmentStatus.InProgress, AppointmentStatus.AwaitingPayment) or
                (AppointmentStatus.AwaitingPayment, AppointmentStatus.Completed) or
                (AppointmentStatus.AwaitingInspection or AppointmentStatus.Scheduled or AppointmentStatus.InProgress, AppointmentStatus.Cancelled);
            yield return [from, to, allowed];
        }
    }

    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void CommandsEnforceEveryTransitionAndLeaveRejectedStateUnchanged(
        AppointmentStatus from, AppointmentStatus to, bool allowed)
    {
        var appointment = At(from);
        var originalPlan = appointment.RepairPlan;
        if (allowed)
        {
            Apply(appointment, to);
            Assert.Equal(to, appointment.Status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => Apply(appointment, to));
            Assert.Equal(from, appointment.Status);
            Assert.Same(originalPlan, appointment.RepairPlan);
        }
    }

    [Fact]
    public void ScheduleRequiresInspectionAndPlanForSameAppointment()
    {
        var appointment = At(AppointmentStatus.AwaitingInspection);
        Assert.Throws<ArgumentNullException>(() => appointment.Schedule(null!, true));
        Assert.Throws<ArgumentException>(() => appointment.Schedule(Plan(Guid.NewGuid()), true));
        Assert.Throws<InvalidOperationException>(() => appointment.Schedule(Plan(appointment.Id), false));
        Assert.Equal(AppointmentStatus.AwaitingInspection, appointment.Status);
        Assert.Null(appointment.RepairPlan);
        var plan = Plan(appointment.Id);
        appointment.Schedule(plan, true);
        Assert.Same(plan, appointment.RepairPlan);
        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CompleteRequiresBothPaymentAndHandover(bool payment, bool handover)
    {
        var appointment = At(AppointmentStatus.AwaitingPayment);
        Assert.Throws<InvalidOperationException>(() => appointment.Complete(payment, handover));
        Assert.Equal(AppointmentStatus.AwaitingPayment, appointment.Status);
        appointment.Complete(true, true);
        Assert.Equal(AppointmentStatus.Completed, appointment.Status);
    }

    [Fact]
    public void AdminCancellationRequiresConfirmationAndReasonEvenAfterInspectionTime()
    {
        var appointment = At(AppointmentStatus.AwaitingInspection);
        Assert.Throws<InvalidOperationException>(() => appointment.CancelByAdmin(false, "Reason"));
        Assert.Throws<ArgumentException>(() => appointment.CancelByAdmin(true, "  "));
        Assert.Equal(AppointmentStatus.AwaitingInspection, appointment.Status);
        Assert.Null(appointment.CancelByOwner(Inspection));
        appointment.CancelByAdmin(true, "Reason");
        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
    }

    [Theory]
    [InlineData(AppointmentStatus.Scheduled)]
    [InlineData(AppointmentStatus.InProgress)]
    [InlineData(AppointmentStatus.AwaitingPayment)]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled)]
    public void CustomerCancellationRejectsEveryOtherState(AppointmentStatus status)
    {
        var appointment = At(status);
        Assert.Null(appointment.CancelByOwner(Inspection.AddTicks(-1)));
        Assert.Equal(status, appointment.Status);
    }

    [Fact]
    public void RepairPlanValidatesPriceDatesAndUtcBoundaries()
    {
        var id = Guid.NewGuid();
        var zero = new RepairPlan(id, 0m, Inspection, Inspection, Inspection);
        Assert.Equal(0m, zero.AgreedPrice);
        Assert.Equal("UAH", zero.Currency);
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepairPlan(id, -0.01m, Inspection, Inspection, Inspection));
        Assert.Throws<ArgumentException>(() => new RepairPlan(id, 1m, Inspection, Inspection.AddTicks(-1), Inspection));
        var nonUtc = Inspection.ToOffset(TimeSpan.FromHours(2));
        Assert.Throws<ArgumentException>(() => new RepairPlan(id, 1m, nonUtc, Inspection, Inspection));
        Assert.Throws<ArgumentException>(() => new RepairPlan(id, 1m, Inspection, nonUtc, Inspection));
        Assert.Throws<ArgumentException>(() => new RepairPlan(id, 1m, Inspection, Inspection, nonUtc));
    }

    [Theory]
    [InlineData(AppointmentStatus.Scheduled)]
    [InlineData(AppointmentStatus.InProgress)]
    [InlineData(AppointmentStatus.AwaitingPayment)]
    public void AgreedPlanCanBeRevisedWithoutChangingStatus(AppointmentStatus status)
    {
        var appointment = At(status);
        var update = Inspection.AddDays(2);
        appointment.ReviseRepairPlan(0m, update, update, update);
        Assert.Equal(status, appointment.Status);
        Assert.Equal(0m, appointment.RepairPlan!.AgreedPrice);
        Assert.Equal("UAH", appointment.RepairPlan.Currency);
        Assert.Equal(update, appointment.RepairPlan.PlannedStartUtc);
        Assert.Equal(update, appointment.RepairPlan.EstimatedFinishUtc);
        Assert.Equal(update, appointment.RepairPlan.UpdatedAtUtc);
        Assert.Throws<ArgumentException>(() => appointment.ReviseRepairPlan(1m, update, update, Inspection));
        Assert.Throws<ArgumentOutOfRangeException>(() => appointment.ReviseRepairPlan(-1m, update, update, update));
        Assert.Equal(0m, appointment.RepairPlan.AgreedPrice);
        Assert.Equal(update, appointment.RepairPlan.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(AppointmentStatus.AwaitingInspection)]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled)]
    public void PlanCannotBeRevisedBeforeAgreementOrAfterTerminalStatus(AppointmentStatus status)
    {
        var appointment = At(status);
        Assert.Throws<InvalidOperationException>(() => appointment.ReviseRepairPlan(1m, Inspection, Inspection, Inspection));
        Assert.Equal(status, appointment.Status);
    }

    [Fact]
    public void InitialAgreementRequiresInspectionAndDoesNotAdvanceStatus()
    {
        var appointment = At(AppointmentStatus.AwaitingInspection);
        var plan = Plan(appointment.Id);
        Assert.Throws<InvalidOperationException>(() => appointment.AgreeRepairPlan(plan, false));
        Assert.Throws<ArgumentException>(() => appointment.AgreeRepairPlan(Plan(Guid.NewGuid()), true));
        Assert.Null(appointment.RepairPlan);
        appointment.AgreeRepairPlan(plan, true);
        Assert.Equal(AppointmentStatus.AwaitingInspection, appointment.Status);
        Assert.Same(plan, appointment.RepairPlan);
        appointment.Schedule(plan, true);
        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
    }

    [Fact]
    public void StatusEventAndHandoverConfirmationValidateAndPreserveFacts()
    {
        var id = Guid.NewGuid();
        var history = AppointmentStatusEvent.ForTransition(id, AppointmentStatus.InProgress, AppointmentStatus.Cancelled, "actor", Inspection, "  reason  ");
        Assert.Equal("reason", history.Reason);
        Assert.Equal(AppointmentStatus.InProgress, history.FromStatus);
        Assert.Equal("actor", history.ActorId);
        Assert.Throws<ArgumentException>(() => AppointmentStatusEvent.ForTransition(id, AppointmentStatus.AwaitingInspection, AppointmentStatus.Completed, "actor", Inspection));
        Assert.Throws<ArgumentException>(() => AppointmentStatusEvent.ForTransition(id, AppointmentStatus.InProgress, AppointmentStatus.Cancelled, "actor", Inspection, "  "));
        var confirmation = new PaymentHandoverConfirmation(id, "actor", Inspection);
        Assert.Equal(id, confirmation.AppointmentId);
        Assert.Equal("actor", confirmation.ActorId);
        Assert.Equal(Inspection, confirmation.PaidAtUtc);
        Assert.Equal(Inspection, confirmation.HandedOverAtUtc);
        Assert.Throws<ArgumentException>(() => new PaymentHandoverConfirmation(id, "actor", Inspection.ToOffset(TimeSpan.FromHours(2))));
    }

    [Fact]
    public void StatusCannotBeAssignedThroughPublicSetterOrCreationDto()
    {
        var setter = typeof(Appointment).GetProperty(nameof(Appointment.Status))!.SetMethod;
        Assert.NotNull(setter);
        Assert.False(setter.IsPublic);
        Assert.DoesNotContain(typeof(Appointment).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(AppointmentStatus)));
        const string json = """{"InspectionStartUtc":"2045-01-02T10:00:00Z","Vin":"1HGCM82633A123456","Make":"Test","Model":"Model","ProblemDescription":"Issue","Status":"Completed"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateAppointmentRequest>(json));
    }

    private static Appointment At(AppointmentStatus status)
    {
        var appointment = new Appointment(Guid.NewGuid(), "owner", Inspection, "1HGCM82633A123456", "Test", "Model", "Issue", Inspection.AddDays(-1));
        if (status == AppointmentStatus.Cancelled)
        {
            appointment.CancelByAdmin(true, "Reason");
            return appointment;
        }
        if (status >= AppointmentStatus.Scheduled) appointment.Schedule(Plan(appointment.Id), true);
        if (status >= AppointmentStatus.InProgress) appointment.StartRepair();
        if (status >= AppointmentStatus.AwaitingPayment) appointment.FinishRepair();
        if (status == AppointmentStatus.Completed) appointment.Complete(true, true);
        return appointment;
    }

    private static RepairPlan Plan(Guid id) => new(id, 100m, Inspection, Inspection.AddDays(1), Inspection);

    private static void Apply(Appointment appointment, AppointmentStatus target)
    {
        switch (target)
        {
            case AppointmentStatus.Scheduled: appointment.Schedule(Plan(appointment.Id), true); break;
            case AppointmentStatus.InProgress: appointment.StartRepair(); break;
            case AppointmentStatus.AwaitingPayment: appointment.FinishRepair(); break;
            case AppointmentStatus.Completed: appointment.Complete(true, true); break;
            case AppointmentStatus.Cancelled: appointment.CancelByAdmin(true, "Reason"); break;
            default: throw new InvalidOperationException("No command transitions to AwaitingInspection.");
        }
    }
}
