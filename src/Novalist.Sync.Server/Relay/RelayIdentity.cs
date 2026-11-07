using Novalist.Sync.Server.Transport;

namespace Novalist.Sync.Server.Relay;

internal sealed record RelayIdentity(string Address, string Issuer, string Subject)
{
    public static RelayIdentity From(HttpContext context) => new(
        ClientAddress.Key(context.Connection.RemoteIpAddress),
        context.User.FindFirst("iss")?.Value ?? "", context.User.FindFirst("sub")?.Value ?? "");

    public IEnumerable<RelayQuotaKey> Keys()
    {
        yield return new(false, Address, "");
        if (Issuer.Length > 0 && Subject.Length > 0) yield return new(true, Issuer, Subject);
    }
}

internal readonly record struct RelayQuotaKey(bool Account, string Identity, string Subject);
