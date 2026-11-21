using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.Application.Appointments;

/// <summary>An interval used when allocating the workshop's interchangeable workplaces.</summary>
public sealed record RepairLoad(Guid Id, AppointmentStatus Status, RepairPeriod Period, DateTimeOffset CreatedAtUtc);

public static class RepairLoadPlanner
{
    /// <summary>Actual and paused repairs take precedence; scheduled repairs keep chronological priority.</summary>
    public static IReadOnlySet<Guid> Conflicts(IReadOnlyCollection<RepairLoad> loads, int capacity)
    {
        var accepted = new List<RepairPeriod>();
        var conflicts = new HashSet<Guid>();
        foreach (var load in loads.OrderBy(x => x.Status == AppointmentStatus.InProgress ? 0 : x.Status == AppointmentStatus.Paused ? 1 : 2)
                     .ThenBy(x => x.Period.Start).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Id))
        {
            if (load.Status != AppointmentStatus.InProgress && !RepairAvailability.Fits(load.Period, accepted, capacity)) conflicts.Add(load.Id);
            if (load.Status != AppointmentStatus.Scheduled || !conflicts.Contains(load.Id)) accepted.Add(load.Period);
        }
        return conflicts;
    }

    /// <summary>Find the first unrefused candidate that fits both the full period and the two-day reserve.</summary>
    public static RepairLoad? FirstPauseOffer(IReadOnlyCollection<RepairLoad> loads, Guid pausedId, DateOnly today,
        DateOnly expectedReturn, int capacity, IReadOnlySet<Guid> declined)
    {
        foreach (var candidate in loads.Where(x => x.Status == AppointmentStatus.Scheduled && !declined.Contains(x.Id))
                     .OrderBy(x => x.Period.Start).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Id))
        {
            var duration = candidate.Period.Finish.DayNumber - candidate.Period.Start.DayNumber;
            if (duration <= 0 || (long)today.DayNumber + duration + 2 > expectedReturn.DayNumber || candidate.Period.Start <= today) continue;
            var proposed = new RepairPeriod(today, today.AddDays(duration));
            var other = loads.Where(x => x.Id != candidate.Id).Select(x => x.Period);
            if (RepairAvailability.Fits(proposed, other, capacity)) return candidate;
        }
        return null;
    }
}
