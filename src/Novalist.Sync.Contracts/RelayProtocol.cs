namespace Novalist.Sync.Contracts;

public static class RelayProtocol
{
    public const string ConnectionHeader = "X-Novalist-Relay-Connection";
    public const string RenewalPath = "/relay/v1/connections/{connectionId:guid}/token";
}
