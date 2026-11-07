using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Hubs;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// The real server (Program.cs) in memory, pointed at the test database and
// configured to trust TestTokens instead of an external identity provider.
public sealed class SyncServer(DatabaseFixture database, TimeProvider? clock = null, DbCommandInterceptor? commands = null,
    HubLifetimeManager<SyncHub>? lifetime = null, long? blobQuota = null, IReadOnlyDictionary<string, string>? settings = null)
    : HttpsWebApplicationFactory
{
    public const string PublicUrl = "https://sync.example/";
    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (clock is not null) builder.ConfigureTestServices(services => services.AddSingleton(clock));
        if (lifetime is not null) builder.ConfigureTestServices(services => services.AddSingleton(lifetime));
        if (commands is not null) builder.ConfigureTestServices(services => services.AddDbContext<SyncDbContext>(options => options.AddInterceptors(commands)));
        builder.UseSetting("Relay:Enabled", "false");
        builder.UseSetting("ConnectionStrings:Default", database.ConnectionString);
        builder.UseSetting("Collaboration:ServerUrl", PublicUrl);
        if (blobQuota is { } quota) builder.UseSetting("BlobStorage:MaxProjectBytes", quota.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var setting in settings ?? new Dictionary<string, string>()) builder.UseSetting(setting.Key, setting.Value);

        builder.ConfigureLogging(logging => logging
            .ClearProviders()
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(Logs));

        builder.ConfigureTestServices(services =>
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.Configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                options.Configuration.SigningKeys.Add(TestTokens.SigningKey);
                options.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                options.TokenValidationParameters.ValidAudience = TestTokens.Audience;
                options.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
            }));
    }

    public HttpClient CreateClient(string? token)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public HubConnection CreateHubConnection(string? token, Guid? deviceId)
    {
        var query = deviceId is null ? "" : $"?{Protocol.DeviceIdParameter}={deviceId}";

        return new HubConnectionBuilder()
            .WithUrl(new Uri(BaseAddress, Protocol.HubPath + query), options =>
            {
                // The in-memory test server speaks HTTP, so long polling instead of WebSockets.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .AddMessagePackProtocol()
            .Build();
    }
}
