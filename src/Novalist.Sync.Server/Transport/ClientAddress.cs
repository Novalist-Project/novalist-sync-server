using System.Net;
using System.Net.Sockets;

namespace Novalist.Sync.Server.Transport;

internal static class ClientAddress
{
    // One IPv4 address (including IPv4-mapped addresses) or one IPv6 /64, so rotating
    // interface addresses inside a prefix cannot evade per-address limits.
    public static string Key(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return address.ToString();
        var bytes = address.GetAddressBytes();
        bytes.AsSpan(8).Clear();
        return new IPAddress(bytes) + "/64";
    }
}
