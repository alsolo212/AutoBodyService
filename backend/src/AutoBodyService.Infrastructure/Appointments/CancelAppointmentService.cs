using AutoBodyService.Application.Appointments;
using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class CancelAppointmentService(ApplicationDbContext db, TimeProvider clock) : ICancelAppointmentService
{
    public async Task<CancelAppointmentResult> CancelOwnAsync(Guid appointmentId, string ownerId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Lock only the owner's row. Other writers remain protected by the xmin concurrency token.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {appointmentId} AND \"UserId\" = {ownerId} FOR UPDATE", cancellationToken);
        var appointment = await db.Appointments.SingleOrDefaultAsync(a => a.Id == appointmentId && a.UserId == ownerId, cancellationToken);
        if (appointment is null) return CancelAppointmentResult.NotFound;

        var now = clock.GetUtcNow();
        var statusEvent = appointment.CancelByOwner(ownerId, now);
        if (statusEvent is null) return CancelAppointmentResult.Conflict;
        db.AppointmentStatusEvents.Add(statusEvent);

        var admins = await (from user in db.Users.AsNoTracking()
                            join userRole in db.Set<IdentityUserRole<string>>() on user.Id equals userRole.UserId
                            join role in db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                            where !user.IsBlocked && (role.Name == ApplicationRoles.Admin || role.Name == ApplicationRoles.SuperAdmin)
                            select user.Id).Distinct().ToListAsync(cancellationToken);
        AddNotification(ownerId, "AppointmentCancelled", "Запис скасовано", "Ваш запис на огляд скасовано.");
        foreach (var admin in admins)
            AddNotification(admin, "AdminAppointmentCancelled", "Запис скасовано", "Клієнт скасував запис на огляд.");

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CancelAppointmentResult.Cancelled;
        }
        catch (DbUpdateConcurrencyException)
        {
            return CancelAppointmentResult.Conflict;
        }

        void AddNotification(string recipient, string type, string title, string message) => db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(), RecipientId = recipient, AppointmentId = appointmentId, Type = type,
            Title = title, Message = message, CreatedAtUtc = now, EventKey = $"appointment:{appointmentId}:{type}:{recipient}"
        });
    }
}
