using System.ComponentModel.DataAnnotations;

namespace Novalist.Sync.Server.Relay;

public sealed class RelayIntrospectionOptions
{
    public string Endpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    [Range(5.0, 30)] public double CheckIntervalSeconds { get; set; } = 30;
    [Range(1.0, 5)] public double TimeoutSeconds { get; set; } = 5;
    [Range(0.0, 900)] public double GracePeriodSeconds { get; set; } = 300;
    [Range(1, 256)] public int MaxConcurrentRequests { get; set; } = 16;
    [Range(128, 1048576)] public int MaxResponseBytes { get; set; } = 16384;

    internal bool Valid(IHostEnvironment environment) =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.Scheme == "https" || (environment.IsDevelopment() && uri.Scheme == "http" && uri.IsLoopback));
}
