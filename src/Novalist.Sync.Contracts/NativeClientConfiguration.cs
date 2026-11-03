namespace Novalist.Sync.Contracts;

public sealed record NativeClientConfiguration(byte ProtocolVersion, string? Authority, string? ClientId, string? Scope,
    bool AuthenticationRequired = true);
