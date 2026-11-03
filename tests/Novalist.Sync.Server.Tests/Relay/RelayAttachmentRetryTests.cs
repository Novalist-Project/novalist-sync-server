using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Server.Relay;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayAttachmentRetryTests
{
    [Fact]
    public async Task MinimumAllowanceConnectsTheFirstRouteWithoutWaitingForRefill()
    {
        await using var server = new RelayTestServer(options => options.RequestsPerMinute = 3);
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        var session = await server.ReadIdAsync(control);
        using var guest = server.Socket("guest");
        var joining = server.ConnectAsync(guest, $"sessions/{session}/guest");
        var route = await server.ReadIdAsync(control);
        using var host = server.Socket();
        await server.ConnectAsync(host, $"sessions/{session}/routes/{route}");
        await joining;
        await AssertTransfersAsync(server, guest, host);
    }

    [Fact]
    public async Task GlobalQuotaRejectionLeavesTheSamePendingRouteAvailableForRetry()
    {
        var clock = new RelayTestClock();
        await using var server = new RelayTestServer(options => options.RequestsPerMinute = 3, clock: clock);
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        var session = await server.ReadIdAsync(control);
        using var guest = server.Socket("guest");
        var joining = server.ConnectAsync(guest, $"sessions/{session}/guest");
        var route = await server.ReadIdAsync(control);
        var quotas = server.Services.GetRequiredService<RelayQuotas>();
        Assert.True(quotas.RequestUpgrade());
        Assert.True(quotas.RequestUpgrade());
        Assert.False(quotas.RequestUpgrade());
        var attach = $"sessions/{session}/routes/{route}";
        await server.RejectedAsync(attach, HttpStatusCode.TooManyRequests);
        Assert.False(joining.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var host = server.Socket();
        await server.ConnectAsync(host, attach);
        await joining;
        await AssertTransfersAsync(server, guest, host);
        Assert.True(quotas.RequestUpgrade());
        Assert.False(quotas.RequestUpgrade());
    }

    private static async Task AssertTransfersAsync(RelayTestServer server, ClientWebSocket guest, ClientWebSocket host)
    {
        await guest.SendAsync(new byte[] { 42 }, WebSocketMessageType.Binary, true, server.Token);
        var bytes = new byte[1];
        Assert.Equal(1, (await host.ReceiveAsync(bytes.AsMemory(), server.Token)).Count);
        Assert.Equal(42, bytes[0]);
        await host.SendAsync(new byte[] { 43 }, WebSocketMessageType.Binary, true, server.Token);
        Assert.Equal(1, (await guest.ReceiveAsync(bytes.AsMemory(), server.Token)).Count);
        Assert.Equal(43, bytes[0]);
    }
}
