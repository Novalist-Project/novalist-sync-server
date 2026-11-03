using System.Net;
using System.Net.Sockets;

namespace Novalist.Sync.Server.Relay;

internal sealed record RelayIdentity(string Address, string Issuer, string Subject)
{
    public static RelayIdentity From(HttpContext context) => new(
        AddressKey(context.Connection.RemoteIpAddress),
        context.User.FindFirst("iss")?.Value ?? "", context.User.FindFirst("sub")?.Value ?? "");

    public IEnumerable<RelayQuotaKey> Keys()
    {
        yield return new(false, Address, "");
        if (Issuer.Length > 0 && Subject.Length > 0) yield return new(true, Issuer, Subject);
    }

    private static string AddressKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return address.ToString();
        var bytes = address.GetAddressBytes();
        bytes.AsSpan(8).Clear();
        return new IPAddress(bytes) + "/64";
    }
}

internal readonly record struct RelayQuotaKey(bool Account, string Identity, string Subject);
