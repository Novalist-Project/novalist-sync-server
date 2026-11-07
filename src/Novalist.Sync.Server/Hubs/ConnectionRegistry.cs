using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Novalist.Sync.Server.Hubs;

// Which user and device each open hub connection belongs to, so access can be
// revoked immediately. Only in memory currently.
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly object _admission = new();

    public void Add(HubCallerContext context, Guid userId, Guid deviceId)
    {
        lock (_admission) _connections[context.ConnectionId] = new Connection(context, userId, deviceId, "");
    }

    // Admits the connection only while the instance, its account and its client address are under their caps.
    public bool TryAdd(HubCallerContext context, Guid userId, Guid deviceId, string address, ConnectionLimits limits)
    {
        lock (_admission)
        {
            var open = _connections.Values.Where(c => c.Context.ConnectionId != context.ConnectionId).ToList();
            if (open.Count >= limits.Total || open.Count(c => c.UserId == userId) >= limits.PerAccount ||
                open.Count(c => c.Address == address) >= limits.PerAddress)
                return false;
            _connections[context.ConnectionId] = new Connection(context, userId, deviceId, address);
            return true;
        }
    }

    // Under the admission lock, so a connection that closed is never counted by a concurrent admission.
    public void Remove(string connectionId)
    {
        lock (_admission) _connections.TryRemove(connectionId, out _);
    }

    public void AbortDevice(Guid deviceId)
    {
        foreach (var connection in _connections.Values.Where(c => c.DeviceId == deviceId))
        {
            connection.Context.Abort();
        }
    }

    public void AbortUser(Guid userId)
    {
        foreach (var connection in _connections.Values.Where(c => c.UserId == userId)) connection.Context.Abort();
    }

    public void AbortUnverifiedUser(Guid userId)
    {
        foreach (var connection in _connections.Values.Where(c => c.UserId == userId))
            if (!connection.Context.Items.TryGetValue("deviceAuthenticated", out var authenticated) || authenticated is not true)
                connection.Context.Abort();
    }

    public IReadOnlyList<string> ConnectionIdsOf(Guid userId) =>
        _connections.Values
            .Where(c => c.UserId == userId)
            .Select(c => c.Context.ConnectionId)
            .ToList();

    public IReadOnlyList<string> ConnectionIdsOfDevice(Guid deviceId) =>
        _connections.Values
            .Where(c => c.DeviceId == deviceId)
            .Select(c => c.Context.ConnectionId)
            .ToList();

    public void RevokeProject(Guid userId, Guid projectId)
    {
        foreach (var connection in _connections.Values.Where(value => value.UserId == userId))
        {
            connection.Projects[projectId] = ProjectConnectionState.Revoked;
        }
    }

    public void JoinProject(string connectionId, Guid projectId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection)) return;
        connection.Projects[projectId] = ProjectConnectionState.Joined;
    }

    public void LeaveProject(string connectionId, Guid projectId)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
            connection.Projects.TryRemove(new(projectId, ProjectConnectionState.Joined));
    }

    public IReadOnlyList<PresenceTarget> PresenceTargets(Guid projectId, string sender, int limit) =>
        _connections.Select(pair => pair.Value).Where(connection => connection.Context.ConnectionId != sender &&
            connection.Projects.TryGetValue(projectId, out var state) && state == ProjectConnectionState.Joined)
            .Take(limit).Select(connection => new PresenceTarget(connection.Context.ConnectionId, connection.DeviceId)).ToArray();

    public IReadOnlyList<string> PresenceRecipients(Guid projectId, IReadOnlySet<Guid> activeDevices, IReadOnlyList<PresenceTarget> targets) =>
        targets.Where(target => _connections.TryGetValue(target.ConnectionId, out var connection) &&
            connection.DeviceId == target.DeviceId && activeDevices.Contains(target.DeviceId) &&
            connection.Projects.TryGetValue(projectId, out var state) && state == ProjectConnectionState.Joined)
            .Select(target => target.ConnectionId).ToArray();

    public bool ProjectRevoked(string connectionId, Guid projectId) =>
        !_connections.TryGetValue(connectionId, out var connection) ||
        connection.Projects.TryGetValue(projectId, out var state) && state == ProjectConnectionState.Revoked;

    private enum ProjectConnectionState { Joined, Revoked }

    private sealed record Connection(HubCallerContext Context, Guid UserId, Guid DeviceId, string Address)
    {
        internal ConcurrentDictionary<Guid, ProjectConnectionState> Projects { get; } = new();
    }
}

public sealed record PresenceTarget(string ConnectionId, Guid DeviceId);

public readonly record struct ConnectionLimits(int Total, int PerAccount, int PerAddress);
