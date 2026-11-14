namespace AutoBodyService.Application.Users;

public sealed record AccountActor(string Id, string SecurityStamp);
public sealed record AdminUserItem(string Id, string Name, string Phone, bool IsAdmin, bool IsBlocked, string Version);
public enum AccountOperationResult { Success, Forbidden, NotFound, Conflict }
public sealed record AccountOperation(AccountOperationResult Result, string? TemporaryPassword = null);

public interface IAdminUsers
{
    Task<(AccountOperationResult Result, IReadOnlyList<AdminUserItem> Users)> SearchAsync(AccountActor actor, string? search, CancellationToken cancellationToken);
    Task<AccountOperation> ResetPasswordAsync(AccountActor actor, string id, string version, CancellationToken cancellationToken);
    Task<AccountOperation> SetAdminAsync(AccountActor actor, string id, string version, bool isAdmin, CancellationToken cancellationToken);
    Task<AccountOperation> SetBlockedAsync(AccountActor actor, string id, string version, bool isBlocked, CancellationToken cancellationToken);
}
