using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Novalist.Sync.Server.Relay;

public static class RelayTransportSecurity
{
    public static IServiceCollection AddRelayTransportSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var proxies = configuration.GetSection("Relay:KnownProxies").Get<string[]>() ?? [];
        return services.Configure<ForwardedHeadersOptions>(options =>
        {
            if (proxies.Length == 0) return;
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.RequireHeaderSymmetry = true;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
        });
    }

    public static IApplicationBuilder UseRelayTransportSecurity(this IApplicationBuilder app, IHostEnvironment environment)
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
            if (context.Request.Path.StartsWithSegments("/relay/v1") && !context.Request.IsHttps && !localDevelopment)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Use HTTPS for relay connections.", context.RequestAborted);
                return;
            }
            if (context.Request.Path.StartsWithSegments("/relay/v1"))
            {
                var status = context.RequestServices.GetRequiredService<RelayQuotas>().Request(RelayIdentity.From(context), false);
                if (status != 0) { context.Response.StatusCode = status; return; }
            }
            await next(context);
        });
    }
}
