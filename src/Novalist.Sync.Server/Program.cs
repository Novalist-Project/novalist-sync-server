using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Updates;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Relay;
using Novalist.Sync.Server.Blobs;
using Novalist.Sync.Server.Transport;

var builder = WebApplication.CreateBuilder(args);

var relayEnabled = builder.Configuration.GetValue("Relay:Enabled", false);
builder.Services.AddTransportSecurity(builder.Configuration);
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
    builder.Services.AddScoped<BlobStore>();
    builder.Services.AddScoped<BlobCollector>();
    builder.Services.AddOptions<BlobStorageOptions>().Bind(builder.Configuration.GetSection(BlobStorageOptions.Section))
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddScoped<PresenceAccess>();
    builder.Services.AddScoped<FieldLeaseAccess>();
    builder.Services.AddOptions<UpdateHistoryOptions>().Bind(builder.Configuration.GetSection(UpdateHistoryOptions.Section))
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddScoped<EphemeralStateCleanup>();
    builder.Services.AddHostedService<EphemeralStateCleanupService>();

    builder.Services.AddScoped<UserDirectory>();
    builder.Services.AddSingleton<AccessDeliveryGate>();
    builder.Services.AddScoped<DeviceRegistry>();
    builder.Services.AddSingleton<DeviceProofServer>();
    builder.Services.AddSingleton<DeviceEnrollmentPolicy>();
    builder.Services.AddSingleton<DeviceEnrollmentChallenges>();
    builder.Services.AddSingleton<DeviceEnrollmentTokens>();
    builder.Services.AddScoped<VerifiedDeviceEnrollment>();
    builder.Services.AddScoped<AccountIdentityReset>();
    builder.Services.AddScoped<ProjectAccess>();
    builder.Services.AddScoped<ProjectEpochAccess>();
    builder.Services.AddScoped<InvitationAccess>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ConnectionRegistry>();
    builder.Services.AddScoped<AccessRevocation>();
    builder.Services.AddSignalR(options =>
    {
        options.AddFilter<HubCallRateFilter>();
        options.AddFilter<ClientErrorFilter>();
        options.MaximumReceiveMessageSize = 128 * 1024;
    }).AddMessagePackProtocol();
}

builder.Services.AddLoginAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseTransportSecurity(app.Environment);
if (relayEnabled) app.UseRelayRequestQuotas();
app.UseAuthentication();
app.UseTransportRequestLimits();
app.UseAuthorization();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(15) });

if (relayEnabled) app.MapRelayEndpoints();
else
{
    app.MapHub<SyncHub>(Protocol.HubPath, options =>
    {
        options.ApplicationMaxBufferSize = 256 * 1024;
        // The client reconnects with a fresh token; a connection never outlives its access token.
        options.CloseOnAuthenticationExpiration = true;
    });
    app.MapDeviceEndpoints();
    app.MapVerifiedDeviceEndpoints();
    app.MapDeviceEnrollmentEndpoint();
    app.MapAccountIdentityResetEndpoints();
    app.MapInvitationEndpoints();
}
app.MapNativeClientEndpoint();

app.Run();
