using System.Threading.Channels;
using System.Diagnostics.CodeAnalysis;

namespace Novalist.Sync.Server.Relay;

internal sealed class RelaySession(RelayIdentity owner, byte[]? hostProof, RelayOptions options)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, RelayRoute> routes = new();
    private bool closed;
    public RelayIdentity Owner { get; } = owner;
    public byte[]? HostProof { get; } = hostProof;
    public Guid Id { get; } = Guid.NewGuid();
    public CancellationTokenSource Stop { get; } = new();
    public Channel<Guid> Requests { get; } = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.MaxPendingRoutesPerSession)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });

    public bool TryAddRoute(RelayReservation reservation, [NotNullWhen(true)] out RelayRoute? route, out int status)
    {
        lock (gate)
        {
            route = null;
            status = 404;
            if (closed) return false;
            status = 503;
            if (routes.Count >= options.MaxRoutesPerSession ||
                routes.Values.Count(value => !value.Attached) >= options.MaxPendingRoutesPerSession) return false;
            var added = new RelayRoute(reservation);
            routes.Add(added.Id, added);
            if (!Requests.Writer.TryWrite(added.Id))
            {
                routes.Remove(added.Id);
                added.Complete();
                return false;
            }
            route = added;
            status = 0;
            return true;
        }
    }

    public RelayRoute? ClaimRoute(Guid id)
    {
        lock (gate)
            return !closed && routes.TryGetValue(id, out var route) && route.Claim() ? route : null;
    }

    public void RemoveRoute(RelayRoute route)
    {
        lock (gate) routes.Remove(route.Id);
        route.Complete();
    }

    public void Close()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            Requests.Writer.TryComplete();
        }
        Stop.Cancel();
    }
}
