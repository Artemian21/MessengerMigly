using Messenger.Application.Abstractions;
using Messenger.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Api.Realtime;

/// <summary>PRS-01/02: in-memory presence for a single instance. A user is online while at least one connection is open;
/// after the last disconnect a grace period avoids flicker on reload, then LastSeenAt is written once.</summary>
public sealed class PresenceTracker(IServiceScopeFactory scopes, IHubContext<ChatHub> hub, IClock clock, IConfiguration configuration,
    ILogger<PresenceTracker> logger) : IPresenceTracker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, HashSet<string>> _connections = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _pendingOffline = [];
    private readonly TimeSpan _grace = TimeSpan.FromSeconds(configuration.GetValue("Presence:GraceSeconds", 7));

    public bool IsOnline(Guid userId)
    {
        lock (_gate)
        {
            return _connections.ContainsKey(userId) || _pendingOffline.ContainsKey(userId);
        }
    }

    public async Task ConnectedAsync(Guid userId, string connectionId)
    {
        bool becameOnline;
        lock (_gate)
        {
            if (_pendingOffline.Remove(userId, out var pending))
            {
                pending.Cancel();
                pending.Dispose();
                becameOnline = false;
            }
            else
            {
                becameOnline = !_connections.ContainsKey(userId);
            }

            if (!_connections.TryGetValue(userId, out var set))
            {
                _connections[userId] = set = [];
            }

            set.Add(connectionId);
        }

        if (becameOnline)
        {
            await hub.Clients.Group(ChatHub.PresenceGroup(userId))
                .SendAsync("PresenceChanged", new { userId, online = true, lastSeenAt = (DateTimeOffset?)null });
        }
    }

    public void Disconnected(Guid userId, string connectionId)
    {
        CancellationTokenSource? pending = null;
        lock (_gate)
        {
            if (_connections.TryGetValue(userId, out var set) && set.Remove(connectionId) && set.Count == 0)
            {
                _connections.Remove(userId);
                pending = new CancellationTokenSource();
                _pendingOffline[userId] = pending;
            }
        }

        if (pending is not null)
        {
            _ = GoOfflineAfterGraceAsync(userId, pending);
        }
    }

    private async Task GoOfflineAfterGraceAsync(Guid userId, CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(_grace, pending.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (!_pendingOffline.TryGetValue(userId, out var current) || current != pending)
            {
                return;
            }

            _pendingOffline.Remove(userId);
            pending.Dispose();
        }

        var now = clock.UtcNow;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MessengerDbContext>();
            await db.UserSet.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, now));
            await hub.Clients.Group(ChatHub.PresenceGroup(userId))
                .SendAsync("PresenceChanged", new { userId, online = false, lastSeenAt = now });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record last seen time");
        }
    }
}
