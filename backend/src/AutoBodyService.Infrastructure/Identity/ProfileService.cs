using AutoBodyService.Application.Profiles;
using Microsoft.AspNetCore.Identity;

namespace AutoBodyService.Infrastructure.Identity;

public sealed class ProfileService(UserManager<ApplicationUser> users) : IProfileService
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

        // Identity saves the password hash, security stamp and flag in the same user update.
        var mustChangePassword = user.MustChangePassword;
        user.MustChangePassword = false;
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded) return PasswordChangeResult.Success;
        user.MustChangePassword = mustChangePassword;
        return result.Errors.Any(error => error.Code == users.ErrorDescriber.ConcurrencyFailure().Code)
            ? PasswordChangeResult.Conflict : PasswordChangeResult.Invalid;
    }
}
