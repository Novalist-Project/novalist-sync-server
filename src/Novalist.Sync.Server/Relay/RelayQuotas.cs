using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Novalist.Sync.Server.Relay;

internal sealed class RelayQuotas
{
    private readonly object gate = new();
    private readonly Dictionary<RelayQuotaKey, RelayQuota> entries = new();
    private readonly RelayOptions options;
    private readonly RelayBucket requests;
    private readonly TimeProvider clock;
    private int sessions;
    private int routes;

    public RelayQuotas(IOptions<RelayOptions> configuration, TimeProvider clock)
    {
        options = configuration.Value;
        this.clock = clock;
        requests = new(clock.GetUtcNow(), options.RequestsPerMinute, options.RequestsPerMinute / 60.0);
    }

    internal int EntryCount { get { lock (gate) return entries.Count; } }

    public int Request(RelayIdentity identity, bool accountOnly)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var keys = identity.Keys().Where(key => key.Account == accountOnly).ToArray();
            var quotas = Resolve(keys, now);
            if (quotas is null) return 503;
            if (quotas.Any(q => q.Requests.Available(now) < 1)) return 429;
            foreach (var quota in quotas) quota.Requests.Consume(1);
            return 0;
        }
    }

    public bool RequestUpgrade()
    {
        lock (gate)
        {
            if (requests.Available(clock.GetUtcNow()) < 1) return false;
            requests.Consume(1);
            return true;
        }
    }

    public int AdmitUpgrade(RelayIdentity identity)
    {
        lock (gate)
        {
            if (requests.Available(clock.GetUtcNow()) < 1) return 429;
            var status = Request(identity, true);
            if (status != 0) return status;
            requests.Consume(1);
            return 0;
        }
    }

    public bool TryReserve(bool session, [NotNullWhen(true)] out RelayReservation? reservation, out int status, params RelayIdentity[] identities)
    {
        lock (gate)
        {
            reservation = null;
            status = 503;
            if (session ? sessions >= options.MaxSessions : routes >= options.MaxRoutes) return false;
            var keys = identities.SelectMany(identity => identity.Keys()).Distinct().ToArray();
            var quotas = Resolve(keys, clock.GetUtcNow());
            if (quotas is null) return false;
            status = 429;
            if (!HasCapacity(keys, quotas, session)) return false;
            if (session) sessions++; else routes++;
            foreach (var quota in quotas)
                if (session) quota.Sessions++; else quota.Routes++;
            reservation = new(this, keys, quotas, session);
            status = 0;
            return true;
        }
    }

    public int AdmitRouteAttachment(RelayReservation reservation, RelayIdentity identity)
    {
        lock (gate)
        {
            if (reservation.IsDisposed) return 410;
            if (requests.Available(clock.GetUtcNow()) < 1) return 429;
            var keys = identity.Keys().Where(key => !reservation.Keys.Contains(key)).ToArray();
            var quotas = Resolve(keys, clock.GetUtcNow());
            if (quotas is null) return 503;
            if (!HasCapacity(keys, quotas, false)) return 429;
            var status = Request(identity, true);
            if (status != 0) return status;
            foreach (var (key, quota) in keys.Zip(quotas))
            {
                quota.Routes++;
                reservation.Keys.Add(key);
                reservation.Quotas.Add(quota);
            }
            requests.Consume(1);
            return 0;
        }
    }

    private bool HasCapacity(RelayQuotaKey[] keys, RelayQuota[] quotas, bool session) => keys.Zip(quotas).All(pair =>
        (session ? pair.Second.Sessions : pair.Second.Routes) < (session
            ? (pair.First.Account ? options.MaxSessionsPerAccount : options.MaxSessionsPerIp)
            : (pair.First.Account ? options.MaxRoutesPerAccount : options.MaxRoutesPerIp)));

    private RelayQuota[]? Resolve(RelayQuotaKey[] keys, DateTimeOffset now)
    {
        if (keys.Count(key => !entries.ContainsKey(key)) + entries.Count > options.MaxQuotaEntries)
        {
            foreach (var stale in entries.Where(entry => entry.Value.Sessions == 0 && entry.Value.Routes == 0 &&
                         now - entry.Value.LastUsed >= TimeSpan.FromMinutes(1)).Select(entry => entry.Key).ToArray())
                entries.Remove(stale);
            if (keys.Count(key => !entries.ContainsKey(key)) + entries.Count > options.MaxQuotaEntries) return null;
        }
        return keys.Select(key =>
        {
            if (!entries.TryGetValue(key, out var quota))
                entries[key] = quota = new(now, key.Account ? options.RequestsPerMinutePerAccount : options.RequestsPerMinutePerIp,
                    options.BurstBytes);
            quota.LastUsed = now;
            return quota;
        }).ToArray();
    }

    internal void Release(IReadOnlyList<RelayQuota> quotas, bool session)
    {
        lock (gate)
        {
            if (session) sessions--; else routes--;
            foreach (var quota in quotas)
            {
                if (session) quota.Sessions--; else quota.Routes--;
                quota.LastUsed = clock.GetUtcNow();
            }
        }
    }

    internal async Task ThrottleAsync(IReadOnlyList<RelayQuota> quotas, int bytes, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan delay;
            lock (gate)
            {
                var now = clock.GetUtcNow();
                var available = quotas.Min(quota => quota.Bytes.Available(now, options.BytesPerSecond));
                if (available >= bytes)
                {
                    foreach (var quota in quotas) { quota.Bytes.Consume(bytes); quota.LastUsed = now; }
                    return;
                }
                delay = TimeSpan.FromSeconds((bytes - available) / options.BytesPerSecond);
            }
            await Task.Delay(delay, clock, ct);
        }
    }
}
