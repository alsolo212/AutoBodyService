using System.Security.Cryptography;
using AutoBodyService.Application.Audit;
using AutoBodyService.Application.Notifications;
using AutoBodyService.Application.Users;
using AutoBodyService.Infrastructure.Notifications;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Identity;

public sealed class AdminUsers(ApplicationDbContext db, UserManager<ApplicationUser> users,
    IAdminAuditWriter audit, TimeProvider clock, INotificationRealtimePublisher publisher) : IAdminUsers
{
    public async Task<(AccountOperationResult Result, IReadOnlyList<AdminUserItem> Users)> SearchAsync(
        AccountActor actor, string? search, CancellationToken cancellationToken)
    {
        // Keep the caller's authority stable until the search has completed.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var caller = (await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actor.Id} FOR SHARE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        if (caller is null || caller.IsBlocked || caller.MustChangePassword || caller.SecurityStamp != actor.SecurityStamp)
            return (AccountOperationResult.Forbidden, []);
        var roles = await users.GetRolesAsync(caller);
        var super = roles.Contains(ApplicationRoles.SuperAdmin);
        if (!super && !roles.Contains(ApplicationRoles.Admin)) return (AccountOperationResult.Forbidden, []);

        var query = db.Users.AsNoTracking().Where(u =>
            db.UserRoles.Any(m => m.UserId == u.Id && db.Roles.Any(r => r.Id == m.RoleId && (r.Name == ApplicationRoles.User || (super && r.Name == ApplicationRoles.Admin)))) &&
            !db.UserRoles.Any(m => m.UserId == u.Id && db.Roles.Any(r => r.Id == m.RoleId && r.Name == ApplicationRoles.SuperAdmin)) &&
            (super || !db.UserRoles.Any(m => m.UserId == u.Id && db.Roles.Any(r => r.Id == m.RoleId && r.Name == ApplicationRoles.Admin))));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(u => EF.Functions.ILike(u.Name, "%" + EscapeLike(term) + "%", "\\") || u.PhoneNormalized.Contains(term));
        }
        var rows = await query.OrderBy(u => u.Name).ThenBy(u => u.Id).Take(100).Select(u => new AdminUserItem(
            u.Id, u.Name, u.PhoneNormalized,
            db.UserRoles.Any(m => m.UserId == u.Id && db.Roles.Any(r => r.Id == m.RoleId && r.Name == ApplicationRoles.Admin)),
            u.IsBlocked, u.ConcurrencyStamp!)).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (AccountOperationResult.Success, rows);
    }

    public Task<AccountOperation> ResetPasswordAsync(AccountActor actor, string id, string version, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, version, "AccountPasswordReset", null, cancellationToken);
    public Task<AccountOperation> SetAdminAsync(AccountActor actor, string id, string version, bool isAdmin, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, version, "AccountAdminChanged", isAdmin, cancellationToken);
    public Task<AccountOperation> SetBlockedAsync(AccountActor actor, string id, string version, bool isBlocked, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, version, "AccountBlockedChanged", isBlocked, cancellationToken);

    private async Task<AccountOperation> ChangeAsync(AccountActor actor, string id, string version, string action, bool? value, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // All administrative mutations acquire both locks in the same order. Re-read
        // roles and stamp only after the locks, preventing stale-authority races.
        var locked = await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" IN ({actor.Id}, {id}) ORDER BY \"Id\" FOR UPDATE")
            .ToListAsync(ct);
        var caller = locked.SingleOrDefault(u => u.Id == actor.Id);
        if (caller is null || caller.IsBlocked || caller.MustChangePassword || caller.SecurityStamp != actor.SecurityStamp)
            return new(AccountOperationResult.Forbidden);
        var callerRoles = await users.GetRolesAsync(caller);
        var super = callerRoles.Contains(ApplicationRoles.SuperAdmin);
        if (!super && (!callerRoles.Contains(ApplicationRoles.Admin) || action != "AccountPasswordReset"))
            return new(AccountOperationResult.Forbidden);
        var target = locked.SingleOrDefault(u => u.Id == id);
        if (target is null) return new(AccountOperationResult.NotFound);
        var targetRoles = await users.GetRolesAsync(target);
        if (targetRoles.Contains(ApplicationRoles.SuperAdmin) ||
            (!targetRoles.Contains(ApplicationRoles.User) && !targetRoles.Contains(ApplicationRoles.Admin)) ||
            (!super && targetRoles.Contains(ApplicationRoles.Admin))) return new(AccountOperationResult.NotFound);
        if (target.ConcurrencyStamp != version) return new(AccountOperationResult.Conflict);

        string? temporary = null;
        IdentityResult result;
        if (action == "AccountPasswordReset")
        {
            // Prefix satisfies the Identity policy; the suffix contributes 192 random bits.
            temporary = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var token = await users.GeneratePasswordResetTokenAsync(target);
            target.MustChangePassword = true;
            result = await users.ResetPasswordAsync(target, token, temporary);
        }
        else if (action == "AccountAdminChanged")
        {
            if (targetRoles.Contains(ApplicationRoles.Admin) == value) return new(AccountOperationResult.Conflict);
            // Some existing administrators have only the Admin role. Revocation
            // leaves them with the same ordinary User capabilities as other accounts.
            if (value == false && !targetRoles.Contains(ApplicationRoles.User))
            {
                result = await users.AddToRoleAsync(target, ApplicationRoles.User);
                if (!result.Succeeded) return new(AccountOperationResult.Conflict);
            }
            result = value == true ? await users.AddToRoleAsync(target, ApplicationRoles.Admin) : await users.RemoveFromRoleAsync(target, ApplicationRoles.Admin);
            if (result.Succeeded) result = await users.UpdateSecurityStampAsync(target);
        }
        else
        {
            if (target.IsBlocked == value) return new(AccountOperationResult.Conflict);
            target.IsBlocked = value == true;
            result = await users.UpdateSecurityStampAsync(target);
        }
        if (!result.Succeeded) return new(AccountOperationResult.Conflict);

        audit.RecordAccountAction(actor.Id, target.Id, action, value);
        var eventId = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        NotificationEvents.Add(db, target.Id, null, NotificationAudience.Personal, action,
            "Зміна безпеки облікового запису", "Адміністрація змінила параметри безпеки вашого облікового запису.", now, $"{eventId}:target");
        var superIds = await (from u in db.Users.AsNoTracking()
                              join m in db.UserRoles on u.Id equals m.UserId
                              join r in db.Roles on m.RoleId equals r.Id
                              where !u.IsBlocked && r.Name == ApplicationRoles.SuperAdmin
                              select u.Id).Distinct().ToListAsync(ct);
        foreach (var recipient in superIds)
            NotificationEvents.Add(db, recipient, null, NotificationAudience.SuperAdministrator, action,
                "Адміністративна дія з обліковим записом", "Виконано дію з безпекою облікового запису. Деталі доступні в журналі аудиту.", now, $"{eventId}:super:{recipient}");
        var recipients = NotificationDispatch.PendingRecipients(db);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        // Includes the affected account so its existing Hub connection is revalidated.
        await NotificationDispatch.AfterCommitAsync(publisher, recipients);
        return new(AccountOperationResult.Success, temporary);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
