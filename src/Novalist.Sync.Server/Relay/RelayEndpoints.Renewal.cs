namespace Novalist.Sync.Server.Relay;

public static partial class RelayEndpoints
{
    private static async Task<IResult> RenewAsync(HttpContext context, Guid connectionId,
        RelayAuthorizations connections, RelayTokenIntrospection introspection, TimeProvider clock)
    {
        var identity = RelayIdentity.From(context);
        if (identity.Subject.Length == 0 || identity.Issuer.Length == 0 || RelayLifetime.ExpiresAt(context) <= clock.GetUtcNow())
            return Results.Unauthorized();
        var connection = connections.Find(connectionId);
        if (connection is null) return Results.NotFound();
        if (connection.Identity.Issuer != identity.Issuer || connection.Identity.Subject != identity.Subject)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var state = await introspection.CheckAsync(context, context.RequestAborted);
        if (state != RelayTokenState.Active)
            return Results.StatusCode(state == RelayTokenState.Unavailable ? 503 : 401);
        if (RelayLifetime.ExpiresAt(context) <= clock.GetUtcNow()) return Results.Unauthorized();
        return connection.Renew(connections.Credential(context)) ? Results.NoContent() : Results.Conflict();
    }
}
