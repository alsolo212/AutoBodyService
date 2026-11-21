using AutoBodyService.Application.Appointments;
using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.IntegrationTests;

public sealed class RepairLoadPlannerTests
{
    private static readonly DateOnly Start = new(2027, 1, 1);
    private static RepairLoad Load(int id, AppointmentStatus status, int start, int finish) =>
        new(new Guid(id, 0, 0, new byte[8]), status, new(Start.AddDays(start), Start.AddDays(finish)), DateTimeOffset.UnixEpoch.AddSeconds(id));

    [Fact]
    public void DelayUsesAllWorkplacesAndFlagsOnlyDisplacedScheduledOrders()
    {
        var delayed = Load(1, AppointmentStatus.InProgress, 0, 6);
        var other = Load(2, AppointmentStatus.InProgress, 0, 2);
        var first = Load(3, AppointmentStatus.Scheduled, 3, 5);
        var later = Load(4, AppointmentStatus.Scheduled, 5, 8);
        Assert.Equal(new[] { later.Id }, RepairLoadPlanner.Conflicts([delayed, other, first, later], 2));
        Assert.Empty(RepairLoadPlanner.Conflicts([delayed, other, first, later], 3));
    }

    [Fact]
    public void PauseOfferSkipsLongAndDeclinedRepairsAndKeepsTwoDayBuffer()
    {
        var pause = Load(1, AppointmentStatus.Paused, 10, 12);
        var longRepair = Load(2, AppointmentStatus.Scheduled, 20, 32);
        var first = Load(3, AppointmentStatus.Scheduled, 21, 29);
        var second = Load(4, AppointmentStatus.Scheduled, 22, 29);
        RepairLoad[] loads = [pause, longRepair, first, second];
        Assert.Equal(first.Id, RepairLoadPlanner.FirstPauseOffer(loads, pause.Id, Start, Start.AddDays(10), 1, new HashSet<Guid>())!.Id);
        Assert.Equal(second.Id, RepairLoadPlanner.FirstPauseOffer(loads, pause.Id, Start, Start.AddDays(10), 1, new HashSet<Guid> { first.Id })!.Id);
        Assert.Null(RepairLoadPlanner.FirstPauseOffer(loads, pause.Id, Start, Start.AddDays(10), 1, new HashSet<Guid> { first.Id, second.Id }));
    }

    [Fact]
    public void PauseOfferChecksEveryDayAndCanUseAnotherWorkplace()
    {
        var pause = Load(1, AppointmentStatus.Paused, 10, 15);
        var otherWork = Load(2, AppointmentStatus.InProgress, 0, 8);
        var midBlocker = Load(3, AppointmentStatus.Scheduled, 3, 5);
        var candidate = Load(4, AppointmentStatus.Scheduled, 20, 28);
        RepairLoad[] loads = [pause, otherWork, midBlocker, candidate];
        Assert.Null(RepairLoadPlanner.FirstPauseOffer(loads, pause.Id, Start, Start.AddDays(10), 2, new HashSet<Guid> { midBlocker.Id }));
        Assert.Equal(candidate.Id, RepairLoadPlanner.FirstPauseOffer(loads, pause.Id, Start, Start.AddDays(10), 3, new HashSet<Guid> { midBlocker.Id })!.Id);
    }

    [Fact]
    public void OverdueTemporaryRepairFlagsPausedReturnAndPreservesItsReservation()
    {
        var temporary = Load(1, AppointmentStatus.InProgress, 0, 12);
        var returning = Load(2, AppointmentStatus.Paused, 10, 15);
        var conflicts = RepairLoadPlanner.Conflicts([temporary, returning], 1);
        Assert.Contains(returning.Id, conflicts); Assert.DoesNotContain(temporary.Id, conflicts);
        Assert.Empty(RepairLoadPlanner.Conflicts([temporary, returning], 2));
    }
}
