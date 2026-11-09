using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.InspectionSchedule;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoBodyService.Infrastructure.InspectionSchedule;

public sealed class InspectionExceptionsService(ApplicationDbContext db) : IInspectionExceptionsService
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    private static readonly TimeSpan SlotLength = TimeSpan.FromMinutes(30);

    public async Task<ExceptionSchedule> GetAsync(DateOnly date, CancellationToken cancellationToken)
    {
        // One query keeps the version and weekly template consistent.
        AutoBodyService.Domain.InspectionSchedule.InspectionSchedule schedule;
        InspectionDateOverride? entry;
        do
        {
            schedule = await db.InspectionSchedules.AsNoTracking().Include(s => s.Hours).SingleAsync(cancellationToken);
            entry = await db.InspectionDateOverrides.AsNoTracking().Include(x => x.Intervals).Include(x => x.Slots)
                .SingleOrDefaultAsync(x => x.Date == date, cancellationToken);
        } while (schedule.Version != await db.InspectionSchedules.AsNoTracking().Select(s => s.Version).SingleAsync(cancellationToken));
        var intervals = entry?.Mode switch
        {
            DateOverrideMode.Closed => [],
            DateOverrideMode.Replace => entry.Intervals.Select(i => new DateInterval(i.Start, i.End)).ToArray(),
            _ => schedule.Hours.Where(h => h.DayOfWeek == date.DayOfWeek).Select(h => new DateInterval(h.Start, h.End)).ToArray()
        };
        var starts = new List<TimeOnly>();
        foreach (var interval in intervals)
            for (var time = interval.Start; time < interval.End; time = time.Add(SlotLength)) starts.Add(time);
        foreach (var slot in (entry?.Slots ?? []).OrderBy(s => s.Start))
        {
            starts.RemoveAll(time => Math.Abs((time.ToTimeSpan() - slot.Start.ToTimeSpan()).Ticks) < SlotLength.Ticks);
            if (slot.Mode == SlotOverrideMode.Open) starts.Add(slot.Start);
        }
        var slots = starts.OrderBy(t => t).SelectMany(t => ToUtcSlots(date, t)).OrderBy(s => s.StartUtc).ToArray();
        var result = entry is null ? null : new InspectionException(date, entry.Mode, entry.Reason,
            entry.Intervals.OrderBy(i => i.Start).Select(i => new DateInterval(i.Start, i.End)).ToArray(),
            entry.Slots.OrderBy(s => s.Start).Select(s => new SlotOverride(s.Start, s.Mode)).ToArray());
        return new ExceptionSchedule(schedule.Version, date, result, slots);
    }

    public async Task<ScheduleUpdateResult> UpdateAsync(Guid version, InspectionException? exception, DateOnly date, CancellationToken cancellationToken)
    {
        if (version == Guid.Empty || (exception is not null && !Valid(exception, date))) return ScheduleUpdateResult.Invalid;
        var schedule = await db.InspectionSchedules.SingleAsync(cancellationToken);
        if (schedule.Version != version) return ScheduleUpdateResult.Conflict;
        var current = await db.InspectionDateOverrides.Include(x => x.Intervals).Include(x => x.Slots)
            .SingleOrDefaultAsync(x => x.Date == date, cancellationToken);
        if (exception is null)
        {
            if (current is not null) db.InspectionDateOverrides.Remove(current);
        }
        else
        {
            if (current is null)
            {
                current = new InspectionDateOverride { Id = Guid.NewGuid(), Date = date };
                db.InspectionDateOverrides.Add(current);
            }
            else
            {
                db.RemoveRange(current.Intervals);

            }
            current.Mode = exception.Mode;
            current.Reason = string.IsNullOrWhiteSpace(exception.Reason) ? null : exception.Reason.Trim();
            current.Intervals = exception.Intervals.Select(i => new InspectionDateInterval { Id = Guid.NewGuid(), OverrideId = current.Id, Start = i.Start, End = i.End }).ToList();
            var existingSlots = current.Slots.ToDictionary(s => s.Start);
            foreach (var old in current.Slots.Where(s => !exception.Slots.Any(x => x.Start == s.Start)).ToArray())
            {
                db.Remove(old);
                current.Slots.Remove(old);
            }
            foreach (var slot in exception.Slots)
            {
                if (existingSlots.TryGetValue(slot.Start, out var existing)) existing.Mode = slot.Mode;
                else
                {
                    var added = new InspectionSlotOverride { Id = Guid.NewGuid(), OverrideId = current.Id, Start = slot.Start, Mode = slot.Mode };
                    current.Slots.Add(added);
                    db.Add(added);
                }
            }
            db.AddRange(current.Intervals);
        }
        schedule.Version = Guid.NewGuid();
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return ScheduleUpdateResult.Conflict; }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_InspectionDateOverrides_Date" })
        { return ScheduleUpdateResult.Conflict; }
        return ScheduleUpdateResult.Success;
    }

    private static bool Valid(InspectionException x, DateOnly date)
    {
        if (x.Date != date || !Enum.IsDefined(x.Mode) || x.Reason?.Length > 500 || x.Intervals.Count > 48 || x.Slots.Count > 48 ||
            (x.Mode != DateOverrideMode.Replace && x.Intervals.Count != 0) ||
            (x.Mode == DateOverrideMode.Replace && x.Intervals.Count == 0) ||
            (x.Mode == DateOverrideMode.Closed && x.Slots.Count != 0)) return false;
        var intervals = x.Intervals.OrderBy(i => i.Start).ToArray();
        for (var i = 0; i < intervals.Length; i++)
            if (intervals[i].Start >= intervals[i].End || !Minute(intervals[i].Start) || !Minute(intervals[i].End) ||
                (intervals[i].End - intervals[i].Start).Ticks % SlotLength.Ticks != 0 ||
                (i > 0 && intervals[i].Start < intervals[i - 1].End)) return false;
        var slots = x.Slots.OrderBy(s => s.Start).ToArray();
        for (var i = 0; i < slots.Length; i++)
            if (!Enum.IsDefined(slots[i].Mode) || !Minute(slots[i].Start) || slots[i].Start.ToTimeSpan() + SlotLength >= TimeSpan.FromDays(1) ||
                (i > 0 && slots[i].Start.ToTimeSpan() - slots[i - 1].Start.ToTimeSpan() < SlotLength)) return false;
        return true;
    }
    private static bool Minute(TimeOnly time) => time.Ticks % TimeSpan.TicksPerMinute == 0;

    private static IEnumerable<EffectiveSlot> ToUtcSlots(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local)) yield break;
        var offsets = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local) : [Zone.GetUtcOffset(local)];
        foreach (var offset in offsets)
        {
            var utc = new DateTimeOffset(local, offset).ToUniversalTime();
            // A slot must remain 30 minutes long in both local and UTC time across DST transitions.
            if (TimeZoneInfo.ConvertTime(utc.Add(SlotLength), Zone).DateTime == local.Add(SlotLength))
                yield return new EffectiveSlot(time, utc);
        }
    }
}
