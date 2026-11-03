using System.Net.Http.Json;
using System.Text.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class AnonymousRelayTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task AnonymousHostRequiresAValidCapability(string? secret)
    {
        await using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Relay:Enabled", "true");
            builder.UseSetting("Relay:RequireAuthentication", "false");
        });
        server.UseKestrel(0);
        using var client = server.CreateClient();
        using var host = new ClientWebSocket();
        if (secret is not null) host.Options.SetRequestHeader("X-Novalist-Relay-Host", secret);
        var address = new UriBuilder(new Uri(client.BaseAddress!, "/relay/v1/host")) { Scheme = "ws" }.Uri;
        await Assert.ThrowsAsync<WebSocketException>(() => host.ConnectAsync(address, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousRelaySettingDoesNotDisableLegacyAuthentication(bool explicitDefault)
    {
        await using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            if (explicitDefault) builder.UseSetting("Relay:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Default", "Host=unused;Database=unused;Username=unused");
            builder.UseSetting("Relay:RequireAuthentication", "false");
        });
        Assert.Throws<OptionsValidationException>(() => server.CreateClient());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnonymousGuestsExchangePacketsButCannotAttachHostRoutes(bool controlUppercase, bool routeUppercase)
    {
        await using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Relay:Enabled", "true");
            builder.UseSetting("Relay:RequireAuthentication", "false");
        });
        server.UseKestrel(0);
        using var http = server.CreateClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        Uri Endpoint(string path) => new UriBuilder(new Uri(http.BaseAddress!, path)) { Scheme = "ws" }.Uri;
        var bytes = RandomNumberGenerator.GetBytes(32);
        bytes[0] = 0xab;
        var secret = Convert.ToHexStringLower(bytes);
        using var control = new ClientWebSocket();
        control.Options.SetRequestHeader("X-Novalist-Relay-Host", controlUppercase ? secret.ToUpperInvariant() : secret);
        await control.ConnectAsync(Endpoint("relay/v1/host"), ct);
        var buffer = new byte[64];
        var message = await control.ReceiveAsync(buffer, ct);
        var session = Encoding.ASCII.GetString(buffer, 0, message.Count);
        using var guest = new ClientWebSocket();
        var joining = guest.ConnectAsync(Endpoint($"relay/v1/sessions/{session}/guest"), ct);
        message = await control.ReceiveAsync(buffer, ct);
        var route = Encoding.ASCII.GetString(buffer, 0, message.Count);
        var attach = Endpoint($"relay/v1/sessions/{session}/routes/{route}");
        using var intruder = new ClientWebSocket();
        intruder.Options.SetRequestHeader("X-Novalist-Relay-Host", new string('0', 64));
        await Assert.ThrowsAsync<WebSocketException>(() => intruder.ConnectAsync(attach, ct));
        using var missingSecret = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => missingSecret.ConnectAsync(attach, ct));
        using var host = new ClientWebSocket();
        host.Options.SetRequestHeader("X-Novalist-Relay-Host", routeUppercase ? secret.ToUpperInvariant() : secret);
        await host.ConnectAsync(attach, ct);
        await joining;
        await guest.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, ct);
        message = await host.ReceiveAsync(buffer, ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer[..message.Count]);
        await host.SendAsync(new byte[] { 4, 5 }, WebSocketMessageType.Binary, true, ct);
        message = await guest.ReceiveAsync(buffer, ct);
        Assert.Equal(new byte[] { 4, 5 }, buffer[..message.Count]);
    }

    [Fact]
    public async Task AnonymousRelayStartsWithoutAnIdentityProviderAndAdvertisesItsPolicy()
    {
        await using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Relay:Enabled", "true");
            builder.UseSetting("Relay:RequireAuthentication", "false");
        });
        using var client = server.CreateClient();
        var configuration = await client.GetFromJsonAsync<JsonElement>("/client-configuration", TestContext.Current.CancellationToken);
        Assert.False(configuration.GetProperty("authenticationRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, configuration.GetProperty("authority").ValueKind);
    }
}
