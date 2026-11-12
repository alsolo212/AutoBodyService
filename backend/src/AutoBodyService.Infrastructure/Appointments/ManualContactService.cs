using AutoBodyService.Application.Appointments;
using AutoBodyService.Application.Audit;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class ManualContactService(ApplicationDbContext db, IAdminAuditWriter audit, TimeProvider clock) : IManualContactService
{
    public async Task<ManualContactResult> RecordAsync(Guid appointmentId, ManualContactReason reason, string actorId, CancellationToken cancellationToken)
    {
        if (appointmentId == Guid.Empty || !Enum.IsDefined(reason) || string.IsNullOrWhiteSpace(actorId)) return ManualContactResult.Invalid;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Use the same row lock as status transitions and customer cancellation.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {appointmentId} FOR UPDATE", cancellationToken);
        var appointment = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment is null) return ManualContactResult.NotFound;
        if (!appointment.CanRecordManualContact()) return ManualContactResult.Conflict;
        db.ManualContactEvents.Add(new ManualContactEvent(appointmentId, reason, actorId, clock.GetUtcNow()));
        audit.RecordManualContact(actorId, appointmentId, reason);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ManualContactResult.Recorded;
    }
}
