using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Updates;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Relay;

var builder = WebApplication.CreateBuilder(args);

var relayEnabled = builder.Configuration.GetValue("Relay:Enabled", false);
builder.Services.AddSingleton(new RelayAuthenticationPolicy(RelayAuthentication.Required(builder.Configuration)));
if (relayEnabled)
{
    builder.Services.AddRelay(builder.Configuration);
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("No connection string found in configuration.");

    builder.Services.AddDbContext<SyncDbContext>(options => options
        .UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention());

    builder.Services.AddScoped<UpdateLog>();

    builder.Services.AddScoped<UserDirectory>();
    builder.Services.AddScoped<DeviceRegistry>();
    builder.Services.AddScoped<ProjectAccess>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ConnectionRegistry>();
    builder.Services.AddScoped<AccessRevocation>();
    builder.Services.AddSignalR(options => options.AddFilter<ClientErrorFilter>()).AddMessagePackProtocol();
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false);

if (!builder.Environment.IsDevelopment() && RelayAuthentication.Required(builder.Configuration))
{
    builder.Services
        .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Validate(
            options => !string.IsNullOrEmpty(options.Authority),
            "Authentication:Schemes:Bearer:Authority must be set in non-development environments.")
        .Validate(
            options => !string.IsNullOrEmpty(options.Audience)
                       || options.TokenValidationParameters.ValidAudiences?.Any() == true,
            "Authentication:Schemes:Bearer:ValidAudiences must be set in non-development environments."
        )
        .ValidateOnStart();
}

builder.Services.AddAuthorization();

var app = builder.Build();

if (relayEnabled) app.UseRelayTransportSecurity(app.Environment);
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(15) });

if (relayEnabled) app.MapRelayEndpoints();
else
{
    app.MapHub<SyncHub>(Protocol.HubPath);
    app.MapDeviceEndpoints();
}
app.MapNativeClientEndpoint();

app.Run();
