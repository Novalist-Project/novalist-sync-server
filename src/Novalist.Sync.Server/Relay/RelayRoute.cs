using System.Net.WebSockets;

namespace Novalist.Sync.Server.Relay;

internal sealed class RelayRoute(RelayReservation reservation)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private bool claimed;
    private bool completed;
    public RelayReservation Reservation { get; } = reservation;
    public CancellationToken Token => stop.Token;
    public Guid Id { get; } = Guid.NewGuid();
    private readonly TaskCompletionSource<WebSocket> host = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<WebSocket> Host => host.Task;
    public Task Done => done.Task;
    public bool Attached { get { lock (gate) return Host.IsCompletedSuccessfully; } }

    public bool Claim()
    {
        lock (gate)
        {
            if (claimed || completed) return false;
            claimed = true;
            return true;
        }
    }

    public bool Attach(WebSocket socket)
    {
        lock (gate) return !completed && claimed && host.TrySetResult(socket);
    }

    public int AdmitAttachment(RelayQuotas quotas, RelayIdentity identity)
    {
        lock (gate) return completed || !claimed ? 410 : quotas.AdmitRouteAttachment(Reservation, identity);
    }

    public void ReleaseClaim()
    {
        lock (gate)
            if (!completed && !Host.IsCompletedSuccessfully) claimed = false;
    }

    public void Complete()
    {
        lock (gate)
        {
            if (completed) return;
            completed = true;
        }
        done.TrySetResult();
        stop.Cancel();
        host.TrySetCanceled();
    }
}
