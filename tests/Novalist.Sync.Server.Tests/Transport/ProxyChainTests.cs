using System.Net;
using Microsoft.AspNetCore.Http;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Transport;

public sealed class ProxyChainTests(DatabaseFixture database)
{
    private const string Proxy = "192.0.2.10";

    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.10")]
    [InlineData("2001:db8:1::/48", "2001:db8:1::10")]
    public async Task TwoTrustedHopsAcceptOneProtocolHeaderAndRecoverTheVisitor(string network, string edge)
    {
        await using var server = new SyncServer(database, settings: Settings(network));

        var result = await ForwardedAsync(server, Proxy, $"198.51.100.7, {edge}");

        Assert.True(result.Request.IsHttps);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), result.Connection.RemoteIpAddress);
        Assert.NotEqual(StatusCodes.Status400BadRequest, result.Response.StatusCode);
    }

    [Fact]
    public async Task TheHopLimitDoesNotConsumeAnUntrustedClaimBeforeTheVisitor()
    {
        await using var server = new SyncServer(database, settings: Settings());

        var result = await ForwardedAsync(server, Proxy, "198.51.100.99, 198.51.100.7, 203.0.113.10");

        Assert.True(result.Request.IsHttps);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), result.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task AnUntrustedImmediateProxyCannotAssertHttpsOrTheVisitor()
    {
        await using var server = new SyncServer(database, settings: Settings());

        var result = await ForwardedAsync(server, "192.0.2.99", "198.51.100.7, 203.0.113.10");

        Assert.Equal(StatusCodes.Status400BadRequest, result.Response.StatusCode);
        Assert.False(result.Request.IsHttps);
        Assert.Equal(IPAddress.Parse("192.0.2.99"), result.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task AnUntrustedIntermediateProxyCannotSupplyAnEarlierVisitor()
    {
        await using var server = new SyncServer(database, settings: Settings());

        var result = await ForwardedAsync(server, Proxy, "198.51.100.7, 192.0.2.99");

        Assert.Equal(IPAddress.Parse("192.0.2.99"), result.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task AnUntrustedIntermediateProxyCannotSupplyAnEarlierHttpsAssertion()
    {
        await using var server = new SyncServer(database, settings: Settings());

        var result = await ForwardedAsync(server, Proxy, "198.51.100.7, 192.0.2.99", "https, http");

        Assert.Equal(StatusCodes.Status400BadRequest, result.Response.StatusCode);
        Assert.False(result.Request.IsHttps);
    }

    [Fact]
    public async Task TwoHopModeStillRejectsPlainHttpFromTheTrustedChain()
    {
        await using var server = new SyncServer(database, settings: Settings());

        var result = await ForwardedAsync(server, Proxy, "198.51.100.7, 203.0.113.10", "http");

        Assert.Equal(StatusCodes.Status400BadRequest, result.Response.StatusCode);
    }

    [Fact]
    public async Task OneHopDefaultStillRequiresSymmetricHeaders()
    {
        await using var server = new SyncServer(database, settings: new Dictionary<string, string>
        {
            ["Transport:KnownProxies:0"] = Proxy
        });

        var result = await ForwardedAsync(server, Proxy, "198.51.100.7, 203.0.113.10");

        Assert.Equal(StatusCodes.Status400BadRequest, result.Response.StatusCode);
    }

    [Fact]
    public async Task VisitorQuotasAreIndependentBehindTheSameTrustedEdge()
    {
        var settings = Settings();
        settings["Transport:RequestsPerMinutePerIp"] = "1";
        await using var server = new SyncServer(database, settings: settings);

        Assert.NotEqual(StatusCodes.Status429TooManyRequests,
            (await ForwardedAsync(server, Proxy, "198.51.100.7, 203.0.113.10")).Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests,
            (await ForwardedAsync(server, Proxy, "198.51.100.8, 203.0.113.10")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status429TooManyRequests,
            (await ForwardedAsync(server, Proxy, "198.51.100.7, 203.0.113.10")).Response.StatusCode);
    }

    private static Dictionary<string, string> Settings(string network = "203.0.113.0/24") => new()
    {
        ["Transport:KnownProxies:0"] = Proxy,
        ["Transport:KnownNetworks:0"] = network,
        ["Transport:ForwardLimit"] = "2"
    };

    private static Task<HttpContext> ForwardedAsync(SyncServer server, string address, string forwardedFor, string protocol = "https") =>
        server.Server.SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Path = Protocol.ClientConfigurationPath;
            context.Request.Scheme = "http";
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            context.Connection.LocalIpAddress = IPAddress.Loopback;
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            context.Request.Headers["X-Forwarded-Proto"] = protocol;
        }, TestContext.Current.CancellationToken);
}
