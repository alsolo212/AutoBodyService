namespace AutoBodyService.Application.Appointments;

/// <summary>A repair occupies every calendar date from start through finish, inclusive.</summary>
public readonly record struct RepairPeriod(DateOnly Start, DateOnly Finish);

/// <summary>Calendar-only capacity calculations; actual occupancy is checked separately.</summary>
public static class RepairAvailability
{
    public static bool Fits(RepairPeriod candidate, IEnumerable<RepairPeriod> reservations, int capacity)
    {
        if (capacity <= 0 || candidate.Finish < candidate.Start) return false;
        var changes = new SortedDictionary<int, int>();
        foreach (var period in reservations)
        {
            if (period.Finish < period.Start) throw new ArgumentException("Invalid repair period.", nameof(reservations));
            if (period.Start > candidate.Finish || period.Finish < candidate.Start) continue;
            var first = Math.Max(period.Start.DayNumber, candidate.Start.DayNumber);
            var afterLast = Math.Min(period.Finish.DayNumber, candidate.Finish.DayNumber) + 1;
            changes[first] = changes.GetValueOrDefault(first) + 1;
            changes[afterLast] = changes.GetValueOrDefault(afterLast) - 1;
        }
        var occupied = 0;
        foreach (var change in changes.Values)
        {
            occupied += change;
            if (occupied >= capacity) return false;
        }
        return true;
    }

    /// <summary>Finds the first full period, allowing the finish beyond the latest start.</summary>
    public static DateOnly? Nearest(DateOnly earliest, DateOnly latest, int durationDays,
        IReadOnlyCollection<RepairPeriod> reservations, int capacity)
    {
        if (durationDays <= 0 || capacity <= 0 || earliest > latest) return null;
        var candidates = reservations.Where(p => p.Finish.DayNumber < DateOnly.MaxValue.DayNumber)
            .Select(p => p.Finish.AddDays(1)).Where(d => d >= earliest && d <= latest)
            .Append(earliest).Distinct().Order();
        foreach (var start in candidates)
        {
            if ((long)start.DayNumber + durationDays > DateOnly.MaxValue.DayNumber) continue;
            if (Fits(new(start, start.AddDays(durationDays)), reservations, capacity)) return start;
        }
        return null;
    }
}
