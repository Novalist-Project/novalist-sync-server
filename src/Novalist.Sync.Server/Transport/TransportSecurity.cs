using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Novalist.Sync.Server.Transport;

public static class TransportSecurity
{
    public static IServiceCollection AddTransportSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var proxies = KnownProxies(configuration);
        services.AddOptions<TransportOptions>().Bind(configuration.GetSection(TransportOptions.Section))
            .ValidateDataAnnotations()
            .Validate(options => proxies.Values.All(proxy => IPAddress.TryParse(proxy, out IPAddress? _)),
                $"{proxies.Key} must contain IP addresses.")
            .Validate(options => options.HubCallBurst >= options.HubCallsPerSecond,
                "Transport:HubCallBurst must be at least Transport:HubCallsPerSecond.")
            .ValidateOnStart();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            if (proxies.Values.Length == 0) return;
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.RequireHeaderSymmetry = true;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies.Values)
                if (IPAddress.TryParse(proxy, out var address)) options.KnownProxies.Add(address);
        });
        services.AddSingleton<TransportRequestLimiter>();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<TransportRequestLimiter>((options, limiter) =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = limiter.Limiter;
        });
        return services;
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

    // After authentication, so account buckets apply.
    public static IApplicationBuilder UseTransportRequestLimits(this IApplicationBuilder app)
    {
        app.UseRateLimiter();
        var limiter = app.ApplicationServices.GetRequiredService<TransportRequestLimiter>();
        return app.Use((context, next) => limiter.ChargeRejectedConnectionTokensAsync(context, next));
    }

    // Relay:KnownProxies is the deprecated name from before persistence mode shared these settings.
    private static ProxySetting KnownProxies(IConfiguration configuration)
    {
        const string shared = TransportOptions.Section + ":KnownProxies";
        var values = configuration.GetSection(shared).Get<string[]>() ?? [];
        return values.Length > 0
            ? new(shared, values)
            : new("Relay:KnownProxies", configuration.GetSection("Relay:KnownProxies").Get<string[]>() ?? []);
    }

    private sealed record ProxySetting(string Key, string[] Values);
}
