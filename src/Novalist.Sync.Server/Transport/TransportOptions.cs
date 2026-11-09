using System.ComponentModel.DataAnnotations;

namespace Novalist.Sync.Server.Transport;

// Transport limits shared by persistence and relay mode. See docs/transport.md.
public sealed class TransportOptions
{
    public const string Section = "Transport";

    // Exact IP addresses of the reverse proxy allowed to send X-Forwarded-For and X-Forwarded-Proto.
    public string[] KnownProxies { get; set; } = [];

    // CIDR ranges of further proxies in front of a known proxy, such as a CDN edge.
    public string[] KnownNetworks { get; set; } = [];

    // Trusted hops to walk back through X-Forwarded-For. 2 allows an edge proxy in KnownNetworks.
    [Range(1, 2)] public int ForwardLimit { get; set; } = 1;

    [Range(1, 1_000_000)] public int RequestsPerMinute { get; set; } = 4096;
    [Range(1, 65536)] public int RequestsPerMinutePerIp { get; set; } = 120;
    [Range(1, 65536)] public int RequestsPerMinutePerAccount { get; set; } = 120;
    [Range(1, 1_000_000)] public int MaxConnections { get; set; } = 4096;
    [Range(1, 65536)] public int MaxConnectionsPerIp { get; set; } = 32;
    [Range(1, 65536)] public int MaxConnectionsPerAccount { get; set; } = 16;
    [Range(1, 10_000)] public int HubCallsPerSecond { get; set; } = 20;
    [Range(1, 100_000)] public int HubCallBurst { get; set; } = 200;
}
