using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Transport;

namespace Novalist.Sync.Server.Tests.Transport;

public sealed class TransportSecurityTests(DatabaseFixture database)
{
    private const string Proxy = "192.0.2.10";

    [Theory]
    [InlineData("http", "203.0.113.1", false, 400)]
    [InlineData("https", "203.0.113.1", false, 200)]
    [InlineData("http", "127.0.0.1", false, 200)]
    [InlineData("http", Proxy, true, 200)]
    [InlineData("http", "203.0.113.1", true, 400)]
    public async Task PersistenceModeRequiresTlsOrLocalDevelopmentAndTrustsOnlyConfiguredProxies(
        string scheme, string address, bool forwarded, int expected)
    {
        await using var server = new SyncServer(database, settings: new Dictionary<string, string> { ["Transport:KnownProxies:0"] = Proxy });

        var status = await SendAsync(server, Protocol.ClientConfigurationPath, scheme, address, forwarded ? ("198.51.100.7", "https") : null);

        Assert.Equal(expected, status == 503 ? 200 : status);
    }

    [Fact]
    public async Task PlainHttpHubNegotiationIsRejectedBeforeAuthentication()
    {
        await using var server = new SyncServer(database);
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("http://localhost/") });

        using var response = await client.PostAsync($"{Protocol.HubPath}/negotiate?negotiateVersion=1", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PerAddressLimitUsesTheForwardedClientOnlyFromATrustedProxy()
    {
        await using var server = new SyncServer(database, settings: new Dictionary<string, string>
        {
            ["Transport:KnownProxies:0"] = Proxy,
            ["Transport:RequestsPerMinutePerIp"] = "1"
        });

        Assert.NotEqual(429, await SendAsync(server, Protocol.ClientConfigurationPath, "http", Proxy, ("198.51.100.1", "https")));
        Assert.NotEqual(429, await SendAsync(server, Protocol.ClientConfigurationPath, "http", Proxy, ("198.51.100.2", "https")));
        Assert.Equal(429, await SendAsync(server, Protocol.ClientConfigurationPath, "http", Proxy, ("198.51.100.1", "https")));
        // An untrusted caller cannot pick a fresh address with its own forwarding headers.
        Assert.NotEqual(429, await SendAsync(server, Protocol.ClientConfigurationPath, "https", "203.0.113.9", ("198.51.100.3", "https")));
        Assert.Equal(429, await SendAsync(server, Protocol.ClientConfigurationPath, "https", "203.0.113.9", ("198.51.100.4", "https")));
    }

    [Fact]
    public async Task PerAccountLimitIsIndependentOfOtherAccounts()
    {
        await using var server = new SyncServer(database, settings: new Dictionary<string, string> { ["Transport:RequestsPerMinutePerAccount"] = "2" });
        using var first = server.CreateClient(TestTokens.Create(Guid.NewGuid().ToString()));
        using var second = server.CreateClient(TestTokens.Create(Guid.NewGuid().ToString()));
        var ct = TestContext.Current.CancellationToken;

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await first.GetAsync(Protocol.ClientConfigurationPath, ct)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await first.GetAsync(Protocol.ClientConfigurationPath, ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await second.GetAsync(Protocol.ClientConfigurationPath, ct)).StatusCode);
    }

    [Fact]
    public async Task InstanceLimitAppliesAcrossAddressesAndAccounts()
    {
        await using var server = new SyncServer(database, settings: new Dictionary<string, string> { ["Transport:RequestsPerMinute"] = "2" });

        Assert.NotEqual(429, await SendAsync(server, Protocol.ClientConfigurationPath, "https", "203.0.113.1", null));
        Assert.NotEqual(429, await SendAsync(server, Protocol.ClientConfigurationPath, "https", "203.0.113.2", null));
        Assert.Equal(429, await SendAsync(server, Protocol.ClientConfigurationPath, "https", "203.0.113.3", null));
    }

    [Fact]
    public async Task RequestsOfAnAdmittedHubConnectionDoNotConsumeTheRequestAllowance()
    {
        var ct = TestContext.Current.CancellationToken;
        // Negotiation and the connect request use two of three; long-polling traffic afterwards is not charged.
        await using var server = new SyncServer(database, settings: new Dictionary<string, string> { ["Transport:RequestsPerMinutePerIp"] = "3" });
        var user = await database.CreateUserAsync();
        await using var hub = server.CreateHubConnection(TestTokens.Create(user.Subject), await database.CreateDeviceAsync(user.Id));
        await hub.StartAsync(ct);

        for (var call = 0; call < 10; call++)
            await hub.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct);

        Assert.Equal(HubConnectionState.Connected, hub.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionCapsRefuseExtraConnectionsUntilOneCloses(bool sameAccount)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database, settings: new Dictionary<string, string>
        {
            [sameAccount ? "Transport:MaxConnectionsPerAccount" : "Transport:MaxConnectionsPerIp"] = "1"
        });
        var firstUser = await database.CreateUserAsync();
        var secondUser = sameAccount ? firstUser : await database.CreateUserAsync();
        var first = server.CreateHubConnection(TestTokens.Create(firstUser.Subject), await database.CreateDeviceAsync(firstUser.Id));
        await using var second = server.CreateHubConnection(TestTokens.Create(secondUser.Subject), await database.CreateDeviceAsync(secondUser.Id));
        await first.StartAsync(ct);
        await first.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct);

        var refused = await RefusalAsync(second, ct);
        Assert.Contains("Too many open connections", refused);

        await first.DisposeAsync();
        await using var retry = server.CreateHubConnection(TestTokens.Create(secondUser.Subject), await database.CreateDeviceAsync(secondUser.Id));
        await retry.StartAsync(ct);
        await retry.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct);
    }

    [Fact]
    public async Task HubCallsBeyondTheConnectionBurstAreRejectedWithoutClosingTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database, settings: new Dictionary<string, string>
        {
            ["Transport:HubCallBurst"] = "3",
            ["Transport:HubCallsPerSecond"] = "1"
        });
        var user = await database.CreateUserAsync();
        await using var hub = server.CreateHubConnection(TestTokens.Create(user.Subject), await database.CreateDeviceAsync(user.Id));
        await hub.StartAsync(ct);

        for (var call = 0; call < 3; call++)
            await hub.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct);
        var error = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct));

        Assert.Contains("Too many requests", error.Message);
        Assert.Equal(HubConnectionState.Connected, hub.State);
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        await hub.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct);
    }

    [Fact]
    public async Task HubConnectionClosesWhenItsAccessTokenExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var user = await database.CreateUserAsync();
        var token = TestTokens.Create(user.Subject, expires: DateTime.UtcNow.AddSeconds(3));
        await using var hub = server.CreateHubConnection(token, await database.CreateDeviceAsync(user.Id));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync(ct);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        Assert.Equal(HubConnectionState.Disconnected, hub.State);
    }

    [Theory]
    [InlineData("Transport:KnownProxies:0", "proxy.example")]
    [InlineData("Transport:MaxConnectionsPerAccount", "0")]
    [InlineData("Transport:HubCallsPerSecond", "0")]
    public async Task InvalidTransportSettingsPreventStartup(string setting, string value)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [setting] = value });
        builder.Services.AddTransportSecurity(builder.Configuration);
        await using var app = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(setting.Split(':')[1], error.Message);
    }

    private static async Task<int> SendAsync(SyncServer server, string path, string scheme, string address, (string For, string Proto)? forwarded)
    {
        var result = await server.Server.SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Path = path;
            context.Request.Scheme = scheme;
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            context.Connection.LocalIpAddress = IPAddress.Loopback;
            if (forwarded is { } headers)
            {
                context.Request.Headers["X-Forwarded-For"] = headers.For;
                context.Request.Headers["X-Forwarded-Proto"] = headers.Proto;
            }
        }, TestContext.Current.CancellationToken);
        return result.Response.StatusCode;
    }

    // A refused connection either fails to start or is closed by the server right after the handshake.
    private static async Task<string> RefusalAsync(HubConnection hub, CancellationToken ct)
    {
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += error => { closed.TrySetResult(error); return Task.CompletedTask; };
        try
        {
            await hub.StartAsync(ct);
            await hub.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct);
        }
        catch (Exception error) { return error.ToString(); }
        return (await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct))?.ToString() ?? "";
    }
}
