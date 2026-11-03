using System.Globalization;
using Microsoft.Extensions.Options;

namespace Novalist.Sync.Server.Relay;

internal sealed class RelayLifetime : IAsyncDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly Task monitor;
    private readonly RelayAuthorizations? connections;
    private readonly RelayAuthorization? authorization;
    private int tokenState;
    public CancellationToken Token => stop.Token;
    public bool IsCancellationRequested => stop.IsCancellationRequested;
    public RelayTokenState TokenState => (RelayTokenState)Volatile.Read(ref tokenState);

    public RelayLifetime(HttpContext context, CancellationToken session = default, CancellationToken route = default)
    {
        var shutdown = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session, route, shutdown);
        var authenticated = RelayAuthentication.Required(context);
        if (authenticated)
        {
            connections = context.RequestServices.GetRequiredService<RelayAuthorizations>();
            authorization = connections.Register(context);
        }
        monitor = authenticated ? MonitorAsync(context) : Task.CompletedTask;
    }

    public static DateTimeOffset ExpiresAt(HttpContext context) =>
        double.TryParse(context.User.FindFirst("exp")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        seconds is >= 0 and < 253402300799 ? DateTimeOffset.UnixEpoch.AddSeconds(seconds) : DateTimeOffset.MinValue;

    public Task CancelAsync() => stop.CancelAsync();

    private Task EndAsync(RelayTokenState state)
    {
        Interlocked.CompareExchange(ref tokenState, (int)state, (int)RelayTokenState.Active);
        return stop.CancelAsync();
    }

    private async Task MonitorAsync(HttpContext context)
    {
        var service = context.RequestServices.GetRequiredService<RelayTokenIntrospection>();
        var options = context.RequestServices.GetRequiredService<IOptions<RelayIntrospectionOptions>>().Value;
        var clock = context.RequestServices.GetRequiredService<TimeProvider>();
        var connection = authorization ?? throw new InvalidOperationException("Missing relay authorization.");
        var grace = TimeSpan.FromSeconds(options.GracePeriodSeconds);
        try
        {
            while (true)
            {
                var credential = connection.Read();
                var untilExpiry = credential.ExpiresAt - clock.GetUtcNow() + grace;
                var delay = TimeSpan.FromSeconds(options.CheckIntervalSeconds);
                if (untilExpiry < delay) delay = untilExpiry;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, stop.Token);
                credential = connection.Read();
                var result = clock.GetUtcNow() >= credential.ExpiresAt
                    ? RelayTokenState.Unavailable
                    : await service.CheckAsync(credential.Token, connection.Identity, stop.Token);
                var state = connection.Observe(credential, result, clock.GetUtcNow(), grace);
                if (state == RelayTokenState.Active) continue;
                if (state == RelayTokenState.Unavailable && clock.GetUtcNow() - credential.ExpiresAt >= grace)
                    state = RelayTokenState.Inactive;
                await EndAsync(state);
                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { await stop.CancelAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (authorization is not null && connections is not null) connections.Remove(authorization);
        try
        {
            await stop.CancelAsync();
            await monitor;
        }
        finally { stop.Dispose(); }
    }
}
