using AutoBodyService.Application.Profiles;
using Microsoft.AspNetCore.Identity;
using AutoBodyService.Infrastructure.Persistence;
using AutoBodyService.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Identity;

public sealed class ProfileService(UserManager<ApplicationUser> users, ApplicationDbContext db, TimeProvider clock) : IProfileService
{
    public async Task<OwnProfile?> GetAsync(string userId)
    {
        var user = await users.FindByIdAsync(userId);
        return user is null || user.IsBlocked ? null : new OwnProfile(user.Name, user.PhoneNormalized);
    }

    public async Task<ProfileUpdateResult> UpdateNameAsync(string userId, string name)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null || user.IsBlocked) return ProfileUpdateResult.NotFound;
        user.Name = name;
        var result = await users.UpdateAsync(user);
        return result.Succeeded ? ProfileUpdateResult.Success : ProfileUpdateResult.Conflict;
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null || user.IsBlocked) return PasswordChangeResult.NotFound;
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal)) return PasswordChangeResult.Invalid;

        // Identity uses this same DbContext; its save and the notification share one transaction.
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync() : null;
        var mustChangePassword = user.MustChangePassword;
        user.MustChangePassword = false;
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded)
        {
            var eventId = Guid.NewGuid();
            NotificationEvents.Add(db, userId, null, "PasswordChanged", "Пароль змінено",
                "Пароль вашого облікового запису змінено. Якщо це були не ви, зв’яжіться з адміністрацією.",
                clock.GetUtcNow(), $"password-change:{eventId}:{userId}");
            await db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return PasswordChangeResult.Success;
        }
        user.MustChangePassword = mustChangePassword;
        return result.Errors.Any(error => error.Code == users.ErrorDescriber.ConcurrencyFailure().Code)
            ? PasswordChangeResult.Conflict : PasswordChangeResult.Invalid;
    }
}
