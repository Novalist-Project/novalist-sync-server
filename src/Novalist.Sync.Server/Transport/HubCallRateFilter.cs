using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Novalist.Sync.Server.Transport;

// A token bucket per hub connection, so one client cannot flood Push, CatchUp or blob calls.
// A streaming call counts once when it starts.
public sealed class HubCallRateFilter(IOptions<TransportOptions> options) : IHubFilter
{
    private const string LimiterItem = "transport.hubCallLimiter";

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (invocationContext.Context.Items.TryGetValue(LimiterItem, out var value) && value is RateLimiter limiter)
        {
            using var lease = limiter.AttemptAcquire();
            if (!lease.IsAcquired) throw new HubException("Too many requests on this connection. Slow down and retry.");
        }
        return await next(invocationContext);
    }

    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var limits = options.Value;
        context.Context.Items[LimiterItem] = new TokenBucketRateLimiter(new()
        {
            TokenLimit = limits.HubCallBurst,
            TokensPerPeriod = limits.HubCallsPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
        return next(context);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        try { await next(context, exception); }
        finally
        {
            if (context.Context.Items.Remove(LimiterItem, out var value) && value is IDisposable limiter) limiter.Dispose();
        }
    }
}
