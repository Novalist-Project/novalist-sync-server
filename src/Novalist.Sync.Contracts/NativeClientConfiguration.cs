namespace Novalist.Sync.Contracts;

// KeyBoundEnrollment: the server accepts first-device proofs whose sign-in nonce the client derived from
// its device keys, so one browser sign-in both signs in and registers the device.
public sealed record NativeClientConfiguration(byte ProtocolVersion, string? Authority, string? ClientId, string? Scope,
    bool AuthenticationRequired = true, bool KeyBoundEnrollment = false);
