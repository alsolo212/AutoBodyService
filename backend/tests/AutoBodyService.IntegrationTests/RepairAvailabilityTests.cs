using AutoBodyService.Application.Appointments;

namespace AutoBodyService.IntegrationTests;

public sealed class RepairAvailabilityTests
{
    private static DateOnly Day(int year, int month, int day) => new(year, month, day);

    [Fact]
    public void FinishDateIsOccupiedAndFollowingDayIsAvailable()
    {
        RepairPeriod[] reservations = [new(Day(2026, 12, 30), Day(2027, 1, 4))];
        Assert.False(RepairAvailability.Fits(new(Day(2027, 1, 4), Day(2027, 1, 5)), reservations, 1));
        Assert.True(RepairAvailability.Fits(new(Day(2027, 1, 5), Day(2027, 1, 6)), reservations, 1));
    }

    [Fact]
    public void FullPeriodAndConcurrentOverlapDetermineCapacity()
    {
        RepairPeriod[] reservations = [new(Day(2027, 2, 2), Day(2027, 2, 5)), new(Day(2027, 2, 5), Day(2027, 2, 8))];
        Assert.False(RepairAvailability.Fits(new(Day(2027, 2, 1), Day(2027, 2, 6)), reservations, 2));
        Assert.True(RepairAvailability.Fits(new(Day(2027, 2, 1), Day(2027, 2, 4)), reservations, 2));
        Assert.True(RepairAvailability.Fits(new(Day(2027, 2, 1), Day(2027, 2, 8)), reservations, 3));
    }

    [Fact]
    public void NearestChecksWholeDurationAndAllowsFinishBeyondHorizon()
    {
        var start = Day(2028, 2, 28);
        RepairPeriod[] reservations = [new(Day(2028, 3, 1), Day(2028, 3, 3))];
        Assert.Equal(Day(2028, 3, 4), RepairAvailability.Nearest(start, Day(2028, 3, 4), 10, reservations, 1));
        Assert.Null(RepairAvailability.Nearest(start, Day(2028, 3, 3), 10, reservations, 1));
        Assert.Equal(start, RepairAvailability.Nearest(start, start, 10, [], 1));
    }

    [Fact]
    public void RejectsInvalidPeriodsCapacityAndDuration()
    {
        var start = Day(2026, 11, 20);
        Assert.False(RepairAvailability.Fits(new(start, start.AddDays(-1)), [], 1));
        Assert.False(RepairAvailability.Fits(new(start, start), [], 0));
        Assert.Null(RepairAvailability.Nearest(start, start.AddYears(1), 0, [], 1));
        Assert.Null(RepairAvailability.Nearest(DateOnly.MaxValue, DateOnly.MaxValue, 1, [], 1));
    }
}
