namespace Novalist.Sync.Server.Relay;

internal sealed class RelayReservation(RelayQuotas owner, RelayQuotaKey[] keys, RelayQuota[] quotas, bool session) : IDisposable
{
    private int disposed;
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
    internal HashSet<RelayQuotaKey> Keys { get; } = keys.ToHashSet();
    internal List<RelayQuota> Quotas { get; } = quotas.ToList();

    public Task ThrottleAsync(int bytes, CancellationToken ct) => owner.ThrottleAsync(Quotas, bytes, ct);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(Quotas, session);
    }
}
