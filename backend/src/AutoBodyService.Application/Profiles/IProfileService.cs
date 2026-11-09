namespace AutoBodyService.Application.Profiles;

public sealed record OwnProfile(string Name, string Phone);
public enum ProfileUpdateResult { Success, NotFound, Conflict }
public enum PasswordChangeResult { Success, NotFound, Invalid, Conflict }

public interface IProfileService
{
    Task<OwnProfile?> GetAsync(string userId);
    Task<ProfileUpdateResult> UpdateNameAsync(string userId, string name);
    Task<PasswordChangeResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword);
}
