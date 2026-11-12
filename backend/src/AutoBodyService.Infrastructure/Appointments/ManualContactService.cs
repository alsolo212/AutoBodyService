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
        if (!await db.Appointments.AnyAsync(a => a.Id == appointmentId, cancellationToken)) return ManualContactResult.NotFound;
        db.ManualContactEvents.Add(new ManualContactEvent(appointmentId, reason, actorId, clock.GetUtcNow()));
        audit.RecordManualContact(actorId, appointmentId, reason);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ManualContactResult.Recorded;
    }
}
