using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Relay;

internal sealed class RelayAuthorizations(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, RelayAuthorization> connections = new();

    public RelayAuthorization Register(HttpContext context)
    {
        var authorization = new RelayAuthorization(RelayIdentity.From(context), Credential(context));
        if (!connections.TryAdd(authorization.Id, authorization)) throw new InvalidOperationException("Relay connection collision.");
        context.Response.Headers[RelayProtocol.ConnectionHeader] = authorization.Id.ToString("N");
        return authorization;
    }

    public RelayAuthorization? Find(Guid id) => connections.GetValueOrDefault(id);

    public RelayCredential Credential(HttpContext context) => new(
        AuthenticationHeaderValue.Parse(context.Request.Headers.Authorization.ToString()).Parameter
            ?? throw new InvalidOperationException("An authenticated relay requires an access token."),
        RelayLifetime.ExpiresAt(context), clock.GetUtcNow());

    public void Remove(RelayAuthorization authorization)
    {
        authorization.Close();
        connections.TryRemove(authorization.Id, out _);
    }
}
