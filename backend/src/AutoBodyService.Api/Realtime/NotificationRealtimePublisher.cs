using System.Security.Claims;
using AutoBodyService.Application.Notifications;
using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;

namespace AutoBodyService.Api.Realtime;

internal sealed class NotificationRealtimePublisher(
    NotificationConnections connections, IServiceScopeFactory scopeFactory, IHubContext<NotificationsHub> hub, TimeProvider clock)
    : INotificationRealtimePublisher
{
    public async Task PublishAsync(IReadOnlyCollection<string> recipients)
    {
        foreach (var (id, connection) in connections.ForRecipients(recipients))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var services = scope.ServiceProvider;
                var signIn = services.GetRequiredService<SignInManager<ApplicationUser>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var revocations = services.GetRequiredService<SessionRevocations>();
                var principal = connection.Principal;
                var session = principal.FindFirstValue(SessionRevocations.ClaimType);
                var user = await signIn.ValidateSecurityStampAsync(principal);
                var roles = user is null ? Array.Empty<string>() : await users.GetRolesAsync(user);
                var cookieRoles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
                if (connection.ExpiresUtc <= clock.GetUtcNow() || user is null || user.Id != connection.UserId ||
                    user.IsBlocked || user.MustChangePassword || session is null ||
                    await revocations.IsRevokedAsync(session, connection.UserId) || !roles.ToHashSet(StringComparer.Ordinal).SetEquals(cookieRoles))
                {
                    connections.Remove(id);
                    connection.Abort();
                    continue;
                }
                // No user data, object IDs, or credentials are sent on the hub. REST enforces current authorization.
                await hub.Clients.Client(id).SendAsync("NotificationsChanged");
            }
            catch
            {
                // Fail closed for this connection; other recipients and committed data are unaffected.
                connections.Remove(id);
                connection.Abort();
            }
        }
    }
}
