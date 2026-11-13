using System.Collections.Concurrent;
using System.Security.Claims;

namespace AutoBodyService.Api.Realtime;

internal sealed record NotificationConnection(string UserId, ClaimsPrincipal Principal, DateTimeOffset ExpiresUtc, Action Abort);

public sealed class NotificationConnections
{
    private readonly ConcurrentDictionary<string, NotificationConnection> connections = new();
    internal void Add(string connectionId, NotificationConnection connection) => connections[connectionId] = connection;
    internal void Remove(string connectionId) => connections.TryRemove(connectionId, out _);
    internal KeyValuePair<string, NotificationConnection>[] ForRecipients(IReadOnlyCollection<string> recipients)
    {
        var ids = recipients.ToHashSet(StringComparer.Ordinal);
        return connections.Where(pair => ids.Contains(pair.Value.UserId)).ToArray();
    }
}
