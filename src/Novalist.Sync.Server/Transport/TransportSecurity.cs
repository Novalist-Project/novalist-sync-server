using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Transport;

public static class TransportSecurity
{
    public static IServiceCollection AddTransportSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TransportOptions>().Bind(configuration.GetSection(TransportOptions.Section))
            .ValidateDataAnnotations()
            .Validate(options => options.KnownProxies.All(proxy => IPAddress.TryParse(proxy, out _)),
                "Transport:KnownProxies must contain IP addresses.")
            .ValidateOnStart();
        var proxies = KnownProxies(configuration);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            if (proxies.Length == 0) return;
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.RequireHeaderSymmetry = true;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies)
                if (IPAddress.TryParse(proxy, out var address)) options.KnownProxies.Add(address);
        });
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PerMinute(_ => "all", value => value.RequestsPerMinute),
                PerMinute(context => ClientAddress.Key(context.Connection.RemoteIpAddress), value => value.RequestsPerMinutePerIp),
                PerMinute(Account, value => value.RequestsPerMinutePerAccount));
        });
    }

    // Before authentication: trust forwarded headers only from configured proxies, then refuse plain HTTP.
    public static IApplicationBuilder UseTransportSecurity(this IApplicationBuilder app, IHostEnvironment environment)
    {
        var localConnection = new object();
        app.Use((context, next) =>
        {
            context.Items[localConnection] =
                context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
                context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local);
            return next(context);
        });
        app.UseForwardedHeaders();
        return app.Use(async (context, next) =>
        {
            var localDevelopment = environment.IsDevelopment() && context.Items[localConnection] is true;
            if (!context.Request.IsHttps && !localDevelopment)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Use HTTPS.", context.RequestAborted);
                return;
            }
            await next(context);
        });
    }

    // Relay:KnownProxies is the deprecated name from before persistence mode shared these settings.
    private static string[] KnownProxies(IConfiguration configuration)
    {
        var shared = configuration.GetSection(TransportOptions.Section + ":KnownProxies").Get<string[]>() ?? [];
        return shared.Length > 0 ? shared : configuration.GetSection("Relay:KnownProxies").Get<string[]>() ?? [];
    }

    // Relay endpoints have their own quotas. Requests that carry a SignalR connection token belong to an
    // already admitted connection, which the connection caps and hub call limits cover.
    internal static bool Counted(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/relay/v1")) return false;
        if (!path.StartsWithSegments(Protocol.HubPath, out var rest)) return true;
        return rest.Equals("/negotiate", StringComparison.OrdinalIgnoreCase) || !context.Request.Query.ContainsKey("id");
    }

    private static string? Account(HttpContext context) =>
        context.User.FindFirst("iss")?.Value is { Length: > 0 } issuer && context.User.FindFirst("sub")?.Value is { Length: > 0 } subject
            ? issuer + "\n" + subject
            : null;

    private static PartitionedRateLimiter<HttpContext> PerMinute(Func<HttpContext, string?> key, Func<TransportOptions, int> limit) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!Counted(context) || key(context) is not { } partition) return RateLimitPartition.GetNoLimiter("");
            var perMinute = limit(context.RequestServices.GetRequiredService<IOptions<TransportOptions>>().Value);
            return RateLimitPartition.GetTokenBucketLimiter(partition, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = perMinute,
                TokensPerPeriod = Math.Max(1, (int)Math.Ceiling(perMinute / 60.0)),
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });
}
