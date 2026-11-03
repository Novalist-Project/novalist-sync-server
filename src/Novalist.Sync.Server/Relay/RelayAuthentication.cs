using System.Security.Cryptography;

namespace Novalist.Sync.Server.Relay;

internal sealed record RelayAuthenticationPolicy(bool Required);

internal static class RelayAuthentication
{
    internal static bool Required(IConfiguration configuration) =>
        !configuration.GetValue("Relay:Enabled", false) || configuration.GetValue("Relay:RequireAuthentication", true);

    internal static bool Required(HttpContext context) => context.RequestServices.GetRequiredService<RelayAuthenticationPolicy>().Required;

    internal static byte[]? HostProof(HttpContext context)
    {
        var values = context.Request.Headers["X-Novalist-Relay-Host"];
        if (values.Count != 1 || values[0] is not { Length: 64 } value || !value.All(Uri.IsHexDigit)) return null;
        return SHA256.HashData(Convert.FromHexString(value));
    }

    internal static bool Owns(RelaySession session, HttpContext context) => session.HostProof is { } expected
        ? HostProof(context) is { } actual && CryptographicOperations.FixedTimeEquals(expected, actual)
        : session.Owner.Subject == context.User.FindFirst("sub")?.Value && session.Owner.Issuer == context.User.FindFirst("iss")?.Value;
}
