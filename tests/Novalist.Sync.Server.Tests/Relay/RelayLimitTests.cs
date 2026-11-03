using System.Net;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayLimitTests
{
    [Theory]
    [InlineData("global", HttpStatusCode.ServiceUnavailable)]
    [InlineData("account", HttpStatusCode.TooManyRequests)]
    [InlineData("ip", HttpStatusCode.TooManyRequests)]
    public async Task SessionReservationsApplyBeforeUpgradeAndReleaseOnDisconnect(string limit, HttpStatusCode expected)
    {
        await using var server = new RelayTestServer(options =>
        {
            if (limit == "global") options.MaxSessions = 1;
            if (limit == "account") options.MaxSessionsPerAccount = 1;
            if (limit == "ip") options.MaxSessionsPerIp = 1;
        });
        using var first = server.Socket();
        await server.ConnectAsync(first, "host");
        var session = await server.ReadIdAsync(first);
        await server.RejectedAsync("host", expected, limit == "account" ? "owner" : "other");
        await first.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", server.Token);
        await server.ClosedAsync(first);
        await server.RejectedAsync($"sessions/{session}/guest", HttpStatusCode.NotFound);
        using var next = await server.ConnectAfterCleanupAsync("host", "owner");
        Assert.NotEqual(session, await server.ReadIdAsync(next));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousAndAuthenticatedRequestsAreRateLimitedBeforeUpgrade(bool anonymous)
    {
        await using var server = new RelayTestServer(options => options.RequestsPerMinutePerIp = 1, anonymous);
        var capability = anonymous ? new string('a', 64) : null;
        using var host = server.Socket(capability: capability);
        await server.ConnectAsync(host, "host");
        await server.ReadIdAsync(host);
        await server.RejectedAsync("host", HttpStatusCode.TooManyRequests, capability: capability);
    }

    [Theory]
    [InlineData("global", HttpStatusCode.ServiceUnavailable)]
    [InlineData("account", HttpStatusCode.TooManyRequests)]
    [InlineData("ip", HttpStatusCode.TooManyRequests)]
    public async Task RouteReservationsCoverPendingGuestsAcrossSessions(string limit, HttpStatusCode expected)
    {
        await using var server = new RelayTestServer(options =>
        {
            if (limit == "global") options.MaxRoutes = 1;
            if (limit == "account") options.MaxRoutesPerAccount = 1;
            if (limit == "ip") options.MaxRoutesPerIp = 1;
        });
        using var firstHost = server.Socket();
        using var secondHost = server.Socket();
        await server.ConnectAsync(firstHost, "host");
        await server.ConnectAsync(secondHost, "host");
        var firstSession = await server.ReadIdAsync(firstHost);
        var secondSession = await server.ReadIdAsync(secondHost);
        using var guest = server.Socket("guest");
        var joining = server.ConnectAsync(guest, $"sessions/{firstSession}/guest");
        await server.ReadIdAsync(firstHost);
        await server.RejectedAsync($"sessions/{secondSession}/guest", expected, "other");
        firstHost.Abort();
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => joining);
        Assert.Equal(HttpStatusCode.Gone, guest.HttpStatusCode);
    }
}
