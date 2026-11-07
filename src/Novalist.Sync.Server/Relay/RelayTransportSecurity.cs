namespace Novalist.Sync.Server.Relay;

public static class RelayTransportSecurity
{
    // Runs after UseTransportSecurity, so the address is the client's and the request is HTTPS.
    public static IApplicationBuilder UseRelayRequestQuotas(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/relay/v1"))
            {
                var status = context.RequestServices.GetRequiredService<RelayQuotas>().Request(RelayIdentity.From(context), false);
                if (status != 0) { context.Response.StatusCode = status; return; }
            }
            await next(context);
        });
}
