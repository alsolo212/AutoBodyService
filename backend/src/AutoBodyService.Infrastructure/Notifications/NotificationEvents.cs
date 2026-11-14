using AutoBodyService.Infrastructure.Identity;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Notifications;

// Stages records in the caller's DbContext; the business transaction owns SaveChanges and commit.
internal static class NotificationEvents
{
    internal static void Add(ApplicationDbContext db, string recipient, Guid? appointmentId, NotificationAudience audience, string type,
        string title, string message, DateTimeOffset now, string eventKey)
        => db.Notifications.Add(new Notification { Id = Guid.NewGuid(), RecipientId = recipient, AppointmentId = appointmentId,
            Audience = audience, Type = type, Title = title, Message = message, CreatedAtUtc = now, EventKey = eventKey });

    internal static async Task<List<string>> AdministratorsAsync(ApplicationDbContext db, CancellationToken cancellationToken) =>
        await (from user in db.Users.AsNoTracking()
               join membership in db.Set<IdentityUserRole<string>>() on user.Id equals membership.UserId
               join role in db.Roles.AsNoTracking() on membership.RoleId equals role.Id
               where !user.IsBlocked && (role.Name == ApplicationRoles.Admin || role.Name == ApplicationRoles.SuperAdmin)
               select user.Id).Distinct().ToListAsync(cancellationToken);

    internal static async Task AddForAdministratorsAsync(ApplicationDbContext db, Guid appointmentId, string type,
        string title, string message, DateTimeOffset now, string eventId, CancellationToken cancellationToken)
    {
        foreach (var recipient in await AdministratorsAsync(db, cancellationToken))
            Add(db, recipient, appointmentId, NotificationAudience.Administrator, type, title, message, now, $"{eventId}:{type}:{recipient}");
    }
}
