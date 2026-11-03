using System.ComponentModel.DataAnnotations;

namespace Novalist.Sync.Server.Relay;

public sealed class RelayOptions
{
    [Range(1, 65536)] public int MaxSessions { get; set; } = 256;
    [Range(1, 65536)] public int MaxSessionsPerIp { get; set; } = 8;
    [Range(1, 65536)] public int MaxSessionsPerAccount { get; set; } = 4;
    [Range(1, 65536)] public int MaxRoutes { get; set; } = 1024;
    [Range(1, 65536)] public int MaxRoutesPerIp { get; set; } = 64;
    [Range(1, 65536)] public int MaxRoutesPerAccount { get; set; } = 32;
    [Range(1, 65536)] public int MaxRoutesPerSession { get; set; } = 16;
    [Range(1, 65536)] public int MaxPendingRoutesPerSession { get; set; } = 4;
    [Range(3, 65536)] public int RequestsPerMinute { get; set; } = 1024;
    [Range(1, 65536)] public int RequestsPerMinutePerIp { get; set; } = 60;
    [Range(1, 65536)] public int RequestsPerMinutePerAccount { get; set; } = 60;
    [Range(16384, int.MaxValue)] public int BytesPerSecond { get; set; } = 8 * 1024 * 1024;
    [Range(16384, int.MaxValue)] public int BurstBytes { get; set; } = 16 * 1024 * 1024;
    [Range(1, 1000000)] public int MaxQuotaEntries { get; set; } = 4096;
    [Range(0.01, 60)] public double RouteTimeoutSeconds { get; set; } = 15;
}
