using AutoBodyService.Application.Appointments;
using AutoBodyService.Application.Audit;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class AdminAppointmentStore(ApplicationDbContext db, IAdminAuditWriter audit, TimeProvider clock) : IAdminAppointmentStore
{
    public async Task<AdminAppointmentResult> ExecuteAsync(Guid id, uint version, string actorId,
        Func<Domain.Appointments.Appointment, DateTimeOffset, AdminAppointmentMutation> mutate, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Customer cancellation uses the same row lock; xmin also rejects other stale writers.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {id} FOR UPDATE", cancellationToken);
        var appointment = await db.Appointments.Include(a => a.RepairPlan).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (appointment is null) return AdminAppointmentResult.NotFound;
        if (db.Entry(appointment).Property<uint>("Version").CurrentValue != version) return AdminAppointmentResult.Conflict;
        var mutation = mutate(appointment, clock.GetUtcNow());
        if (mutation.Result != AdminAppointmentResult.Success) return mutation.Result;
        if (mutation.StatusEvent is null && mutation.Confirmation is null && !mutation.PriceChanged && !mutation.DatesChanged)
            return AdminAppointmentResult.Success;
        if (mutation.StatusEvent is { } statusEvent)
        {
            db.AppointmentStatusEvents.Add(statusEvent);
            audit.RecordAppointmentStatusChange(actorId, id, statusEvent.FromStatus!.Value, statusEvent.ToStatus);
        }
        if (mutation.Confirmation is not null) db.PaymentHandoverConfirmations.Add(mutation.Confirmation);
        if (mutation.PriceChanged || mutation.DatesChanged)
            audit.RecordRepairPlanChange(actorId, id, mutation.PriceChanged, mutation.DatesChanged);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AdminAppointmentResult.Success;
        }
        catch (DbUpdateConcurrencyException) { return AdminAppointmentResult.Conflict; }
    }
}
