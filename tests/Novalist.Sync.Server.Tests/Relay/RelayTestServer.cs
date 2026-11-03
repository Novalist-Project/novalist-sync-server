using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Novalist.Sync.Server.Relay;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Relay;

internal sealed class RelayTestServer : WebApplicationFactory<Program>
{
    private readonly Action<RelayOptions>? configure;
    private readonly Action<RelayIntrospectionOptions>? introspection;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? response;
    private readonly bool anonymous;
    private readonly bool realIntrospection;
    private readonly TimeProvider? clock;
    private readonly CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    public CancellationToken Token => deadline.Token;
    public Uri Address { get; }
    public CapturedLogs Logs { get; } = new();

    public RelayTestServer(Action<RelayOptions>? configure = null, bool anonymous = false,
        Action<RelayIntrospectionOptions>? introspection = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? response = null, bool realIntrospection = false,
        TimeProvider? clock = null)
    {
        this.configure = configure;
        this.anonymous = anonymous;
        this.introspection = introspection;
        this.response = response;
        this.realIntrospection = realIntrospection;
        this.clock = clock;
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        UseKestrel(0);
        using var client = CreateClient();
        Address = client.BaseAddress!;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Relay:Enabled", "true");
        builder.UseSetting("Relay:RequireAuthentication", (!anonymous).ToString());
        builder.UseSetting("Relay:Introspection:Endpoint", "https://idp.test/introspect");
        builder.UseSetting("Relay:Introspection:ClientId", "relay-test");
        builder.UseSetting("Relay:Introspection:ClientSecret", "relay-test-secret");
        builder.ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            if (configure is not null) services.PostConfigure(configure);
            if (clock is not null) services.AddSingleton(clock);
            if (introspection is not null) services.PostConfigure(introspection);
            if (!realIntrospection) services.AddHttpClient("RelayIntrospection")
                    .ConfigurePrimaryHttpMessageHandler(() => new RelayIntrospectionHandler(response));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.Configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                options.Configuration.SigningKeys.Add(TestTokens.SigningKey);
                options.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                options.TokenValidationParameters.ValidAudience = TestTokens.Audience;
                options.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
            });
        });
    }

    public ClientWebSocket Socket(string? subject = "owner", DateTime? expires = null, string? capability = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (!anonymous && subject is not null)
            socket.Options.SetRequestHeader("Authorization", "Bearer " + TestTokens.Create(subject, expires: expires));
        if (capability is not null) socket.Options.SetRequestHeader("X-Novalist-Relay-Host", capability);
        return socket;
    }

    public Task ConnectAsync(ClientWebSocket socket, string path) =>
        socket.ConnectAsync(new UriBuilder(new Uri(Address, "/relay/v1/" + path)) { Scheme = "ws" }.Uri, Token);

    public async Task<ClientWebSocket> ConnectAfterCleanupAsync(string path, string subject)
    {
        for (var attempt = 0; ; attempt++)
        {
            var socket = Socket(subject);
            try { await ConnectAsync(socket, path); return socket; }
            catch (WebSocketException) when (attempt < 20 && !Token.IsCancellationRequested &&
                socket.HttpStatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                socket.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
            }
            catch { socket.Dispose(); throw; }
        }
    }

    public async Task<Guid> ReadIdAsync(ClientWebSocket socket)
    {
        var bytes = new byte[64];
        var message = await socket.ReceiveAsync(bytes.AsMemory(), Token);
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        Assert.True(message.EndOfMessage);
        return Guid.ParseExact(Encoding.ASCII.GetString(bytes, 0, message.Count), "N");
    }

    public async Task RejectedAsync(string path, HttpStatusCode status, string? subject = "owner", string? capability = null)
    {
        using var socket = Socket(subject, capability: capability);
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(socket, path));
        Assert.Equal(status, socket.HttpStatusCode);
    }

    public async Task ClosedAsync(ClientWebSocket socket)
    {
        try
        {
            Assert.Equal(WebSocketMessageType.Close, (await socket.ReceiveAsync(new byte[1].AsMemory(), Token)).MessageType);
            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "acknowledged", Token);
        }
        catch (WebSocketException) { Assert.Equal(WebSocketState.Aborted, socket.State); }
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { deadline.Dispose(); }
    }
}
