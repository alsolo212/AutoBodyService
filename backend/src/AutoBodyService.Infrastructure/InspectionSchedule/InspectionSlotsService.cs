using AutoBodyService.Application.InspectionSchedule;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.InspectionSchedule;

public sealed class InspectionSlotsService(
    IInspectionExceptionsService schedules,
    ApplicationDbContext db,
    TimeProvider clock) : IInspectionSlotsService
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);

    public async Task<IReadOnlyList<InspectionSlot>> GetAvailableAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var schedule = await schedules.GetAsync(date, cancellationToken);
        var now = clock.GetUtcNow();
        var candidates = schedule.TemplateSlots.Where(s => s.StartUtc > now).ToArray();
        if (candidates.Length == 0) return [];

        var from = candidates[0].StartUtc - Duration;
        var to = candidates[^1].StartUtc + Duration;
        var occupied = await db.Appointments.AsNoTracking()
            .Where(a => a.Status != AppointmentStatus.Cancelled && a.InspectionStartUtc > from && a.InspectionStartUtc < to)
            .Select(a => a.InspectionStartUtc).ToArrayAsync(cancellationToken);
        return candidates.Where(slot => !occupied.Any(start => start < slot.StartUtc + Duration && slot.StartUtc < start + Duration))
            .Select(slot => new InspectionSlot(slot.StartUtc, slot.StartUtc + Duration)).ToArray();
    }

    public async Task<IReadOnlyList<InspectionDayAvailability>> GetMonthAsync(int year, int month, CancellationToken cancellationToken)
    {
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var days = await schedules.GetRangeAsync(from, to, cancellationToken);
        var now = clock.GetUtcNow();
        var candidates = days.SelectMany(day => day.TemplateSlots).Where(slot => slot.StartUtc > now).ToArray();
        if (candidates.Length == 0) return days.Select(day => new InspectionDayAvailability(day.Date, false)).ToArray();
        var first = candidates.Min(slot => slot.StartUtc) - Duration;
        var last = candidates.Max(slot => slot.StartUtc) + Duration;
        var occupied = await db.Appointments.AsNoTracking()
            .Where(a => a.Status != AppointmentStatus.Cancelled && a.InspectionStartUtc > first && a.InspectionStartUtc < last)
            .Select(a => a.InspectionStartUtc).ToArrayAsync(cancellationToken);
        return days.Select(day => new InspectionDayAvailability(day.Date, day.TemplateSlots.Any(slot =>
            slot.StartUtc > now && !occupied.Any(start => start < slot.StartUtc + Duration && slot.StartUtc < start + Duration)))).ToArray();
    }
}
