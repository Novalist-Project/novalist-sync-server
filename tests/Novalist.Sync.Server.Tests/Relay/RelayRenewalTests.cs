using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayRenewalTests
{
    [Theory]
    [InlineData("control")]
    [InlineData("guest")]
    [InlineData("attach")]
    public async Task RenewingEachSocketKeepsTheSessionAndRoutePastTheOriginalTokenExpiry(string expiring)
    {
        var clock = new RelayTestClock();
        await using var server = new RelayTestServer(clock: clock, introspection: options => options.GracePeriodSeconds = 0);
        var expiry = clock.GetUtcNow().UtcDateTime.AddSeconds(10);
        using var control = server.Socket(expires: expiring == "control" ? expiry : null);
        await server.ConnectAsync(control, "host");
        var session = await server.ReadIdAsync(control);
        using var guest = server.Socket("guest", expires: expiring == "guest" ? expiry : null);
        var joining = server.ConnectAsync(guest, $"sessions/{session}/guest");
        var route = await server.ReadIdAsync(control);
        using var host = server.Socket(expires: expiring == "attach" ? expiry : null);
        await server.ConnectAsync(host, $"sessions/{session}/routes/{route}");
        await joining;
        var socket = expiring == "control" ? control : expiring == "guest" ? guest : host;
        Assert.True(socket.HttpResponseHeaders!.TryGetValue("X-Novalist-Relay-Connection", out var values));
        var connection = Guid.Parse(Assert.Single(values));
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(expiring == "guest" ? "guest" : "owner"));
        using var renewed = await http.PostAsync($"/relay/v1/connections/{connection}/token", null, server.Token);
        Assert.Equal(HttpStatusCode.NoContent, renewed.StatusCode);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(clock.GetUtcNow().UtcDateTime > expiry);
        await guest.SendAsync(new byte[] { 42 }.AsMemory(), WebSocketMessageType.Binary, true, server.Token);
        var bytes = new byte[1];
        await host.ReceiveAsync(bytes.AsMemory(), server.Token);
        Assert.Equal(42, bytes[0]);
        using var next = server.Socket("next");
        var nextJoin = server.ConnectAsync(next, $"sessions/{session}/guest");
        var nextRoute = await server.ReadIdAsync(control);
        using var nextHost = server.Socket();
        await server.ConnectAsync(nextHost, $"sessions/{session}/routes/{nextRoute}");
        await nextJoin;
    }

    [Theory]
    [InlineData("other", false, HttpStatusCode.Forbidden)]
    [InlineData("owner", true, HttpStatusCode.Unauthorized)]
    [InlineData(null, false, HttpStatusCode.Unauthorized)]
    public async Task RenewalRequiresAFreshTokenForTheOriginalAccount(string? subject, bool expired, HttpStatusCode expected)
    {
        await using var server = new RelayTestServer();
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        await server.ReadIdAsync(control);
        var connection = Assert.Single(control.HttpResponseHeaders!["X-Novalist-Relay-Connection"]);
        using var http = server.CreateClient();
        if (subject is not null) http.DefaultRequestHeaders.Authorization = new("Bearer",
            TestTokens.Create(subject, expires: expired ? DateTime.UtcNow.AddSeconds(-1) : null));
        using var response = await http.PostAsync($"/relay/v1/connections/{connection}/token", null, server.Token);
        Assert.Equal(expected, response.StatusCode);
    }
}
