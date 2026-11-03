using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Server.Relay;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayOutageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeriodicChecksCloseAtTheGraceDeadlineAndRecoveryResetsIt(bool recover)
    {
        var unavailable = 0;
        var checks = 0;
        var clock = new RelayTestClock();
        await using var server = new RelayTestServer(clock: clock,
            introspection: options => { options.CheckIntervalSeconds = 5; options.GracePeriodSeconds = 60; },
            response: (_, _) =>
            {
                Interlocked.Increment(ref checks);
                return Task.FromResult(new HttpResponseMessage(Volatile.Read(ref unavailable) == 0
                    ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("{\"active\":true}") });
            });
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        var session = await server.ReadIdAsync(control);
        await clock.NextDelayAsync();
        await clock.NextDelayAsync();
        Assert.Equal(1, Volatile.Read(ref checks));
        Interlocked.Exchange(ref unavailable, 1);
        await CheckAsync(55);
        if (recover)
        {
            Interlocked.Exchange(ref unavailable, 0);
            await CheckAsync(5);
            Interlocked.Exchange(ref unavailable, 1);
            await CheckAsync(55);
        }
        clock.Advance(TimeSpan.FromSeconds(5));
        await server.ClosedAsync(control);
        Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, control.CloseStatus);
        Assert.Null(server.Services.GetRequiredService<RelaySessions>().Find(session));
        Assert.Equal(recover ? 5 : 3, Volatile.Read(ref checks));

        async Task CheckAsync(int seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            await clock.NextDelayAsync();
            await clock.NextDelayAsync();
            Assert.NotNull(server.Services.GetRequiredService<RelaySessions>().Find(session));
        }
    }
}
