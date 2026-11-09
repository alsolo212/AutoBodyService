using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.IntegrationTests;

public sealed class CancelAppointmentDomainTests
{
    private static readonly DateTimeOffset Inspection = new(2045, 1, 2, 10, 0, 0, TimeSpan.Zero);
    private static Appointment Create() => new(Guid.NewGuid(), "owner", Inspection, "1HGCM82633A123456", "Test", "Model", "Issue", Inspection.AddDays(-1));

    [Fact]
    public void OwnerCancelsBeforeInspectionAndRecordsTransitionExactlyOnce()
    {
        var appointment = Create(); var now = Inspection.AddTicks(-1);
        var change = appointment.CancelByOwner("owner", now);
        Assert.NotNull(change); Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.Equal(AppointmentStatus.AwaitingInspection, change.FromStatus); Assert.Equal(AppointmentStatus.Cancelled, change.ToStatus);
        Assert.Equal("owner", change.ActorId); Assert.Equal(now, change.ChangedAtUtc); Assert.Null(change.Reason);
        Assert.Equal(2, appointment.StatusEvents.Count); Assert.Null(appointment.CancelByOwner("owner", now)); Assert.Equal(2, appointment.StatusEvents.Count);
    }

    [Fact]
    public void ForeignOwnerAndInspectionBoundaryCannotChangeStatusOrHistory()
    {
        var appointment = Create();
        Assert.Null(appointment.CancelByOwner("other", Inspection.AddTicks(-1)));
        Assert.Null(appointment.CancelByOwner("owner", Inspection));
        Assert.Null(appointment.CancelByOwner("owner", Inspection.AddTicks(1)));
        Assert.Equal(AppointmentStatus.AwaitingInspection, appointment.Status); Assert.Single(appointment.StatusEvents);
    }

    [Fact]
    public void ScheduledAppointmentCannotBeCancelledByOwner()
    {
        var appointment = Create();
        appointment.Schedule(new RepairPlan(appointment.Id, 100m, Inspection, Inspection.AddDays(1), Inspection.AddDays(-1)));
        Assert.Null(appointment.CancelByOwner("owner", Inspection.AddTicks(-1)));
        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status); Assert.Single(appointment.StatusEvents);
    }
}
