using System.Security.Claims;
using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;

namespace AutoBodyService.Api.Realtime;

[Authorize(Policy = "User")]
public sealed class NotificationsHub : Hub
{
    private readonly NotificationConnections connections;
    public NotificationsHub(NotificationConnections connections) => this.connections = connections;

    public override async Task OnConnectedAsync()
    {
        var id = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var session = Context.User?.FindFirstValue(SessionRevocations.ClaimType);
        var ticket = await Context.GetHttpContext()!.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (id is null || session is null || ticket.Properties?.ExpiresUtc is not { } expires)
        {
            Context.Abort(); return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{id}");
        connections.Add(Context.ConnectionId, new NotificationConnection(id, Context.User!, expires, Context.Abort));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
