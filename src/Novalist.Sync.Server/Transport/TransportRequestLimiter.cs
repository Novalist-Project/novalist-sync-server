using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Transport;

// Request buckets per instance, client address and account, shared by the built-in rate limiting
// middleware and the check for requests that claim to belong to an open hub connection.
public sealed class TransportRequestLimiter : IDisposable
{
    private static readonly object ChargeItem = new();

    public TransportRequestLimiter() => Limiter = PartitionedRateLimiter.CreateChained(
        PerMinute(_ => "all", value => value.RequestsPerMinute),
        PerMinute(context => ClientAddress.Key(context.Connection.RemoteIpAddress), value => value.RequestsPerMinutePerIp),
        PerMinute(Account, value => value.RequestsPerMinutePerAccount));

    public PartitionedRateLimiter<HttpContext> Limiter { get; }

    // Requests carrying a SignalR connection token are free while the token belongs to an admitted
    // connection. A token the hub rejects is charged afterwards, and once the caller's allowance is
    // used up further tokens are refused up front, so made-up tokens cannot bypass the limits.
    public async Task ChargeRejectedConnectionTokensAsync(HttpContext context, RequestDelegate next)
    {
        if (!HasConnectionToken(context)) { await next(context); return; }
        context.Items[ChargeItem] = true;
        try
        {
            using (var available = Limiter.AttemptAcquire(context, 0))
            {
                if (!available.IsAcquired)
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }
            }
            await next(context);
            if (context.Response.StatusCode is >= 400 and < 500) Limiter.AttemptAcquire(context).Dispose();
        }
        finally { context.Items.Remove(ChargeItem); }
    }

    public void Dispose() => Limiter.Dispose();

    // Relay endpoints have their own quotas.
    internal static bool Counted(HttpContext context) =>
        !context.Request.Path.StartsWithSegments("/relay/v1") && (!HasConnectionToken(context) || context.Items.ContainsKey(ChargeItem));

    private static bool HasConnectionToken(HttpContext context) =>
        context.Request.Path.StartsWithSegments(Protocol.HubPath, out var rest) &&
        !rest.Equals("/negotiate", StringComparison.OrdinalIgnoreCase) && context.Request.Query.ContainsKey("id");

    private static string? Account(HttpContext context) =>
        context.User.FindFirst("iss")?.Value is { Length: > 0 } issuer && context.User.FindFirst("sub")?.Value is { Length: > 0 } subject
            ? issuer + "\n" + subject
            : null;

    private static PartitionedRateLimiter<HttpContext> PerMinute(Func<HttpContext, string?> key, Func<TransportOptions, int> limit) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!Counted(context) || key(context) is not { } partition) return RateLimitPartition.GetNoLimiter("");
            var perMinute = limit(context.RequestServices.GetRequiredService<IOptions<TransportOptions>>().Value);
            return RateLimitPartition.GetTokenBucketLimiter(partition, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = perMinute,
                TokensPerPeriod = Math.Max(1, (int)Math.Ceiling(perMinute / 60.0)),
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });
}
