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
}
