using AutoBodyService.Application.Appointments;
using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.IntegrationTests;

public sealed class RepairPlanningCommandTests
{
    [Fact]
    public async Task CalendarDurationAcrossKyivDstUsesLocalMidnights()
    {
        var now = new DateTimeOffset(2027, 3, 25, 12, 0, 0, TimeSpan.Zero);
        var appointment = new Appointment(Guid.NewGuid(), "owner", now.AddDays(1), null, "Test", "Car", "Issue", now);
        var store = new InMemoryMutationStore(appointment, now);
        var result = await new AdminAppointmentCommands(store).ScheduleAsync(appointment.Id, 1, "actor",
            new(new DateOnly(2027, 3, 27), 3, 100m, true), default);
        Assert.Equal(AdminAppointmentResult.Success, result);
        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
        Assert.Equal(new DateTimeOffset(2027, 3, 26, 22, 0, 0, TimeSpan.Zero), appointment.RepairPlan!.PlannedStartUtc);
        Assert.Equal(new DateTimeOffset(2027, 3, 29, 21, 0, 0, TimeSpan.Zero), appointment.RepairPlan.EstimatedFinishUtc);
        Assert.True(appointment.RepairPlan.CalendarDatesOnly);
        Assert.Equal(AppointmentStatus.Scheduled, store.Mutation!.StatusEvent!.ToStatus);
    }

    private sealed class InMemoryMutationStore(Appointment appointment, DateTimeOffset now) : IAdminAppointmentStore
    {
        public AdminAppointmentMutation? Mutation { get; private set; }
        public Task<AdminAppointmentResult> ExecuteAsync(Guid id, uint version, string actorId,
            Func<Appointment, DateTimeOffset, AdminAppointmentMutation> mutate, CancellationToken cancellationToken)
        {
            Mutation = mutate(appointment, now);
            return Task.FromResult(Mutation.Result);
        }
    }
}
