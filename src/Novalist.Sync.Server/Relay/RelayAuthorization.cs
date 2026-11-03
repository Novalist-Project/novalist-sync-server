namespace Novalist.Sync.Server.Relay;

internal sealed record RelayCredential(string Token, DateTimeOffset ExpiresAt, DateTimeOffset VerifiedAt);

internal sealed class RelayAuthorization(RelayIdentity identity, RelayCredential initial)
{
    private readonly object gate = new();
    private RelayCredential credential = initial;
    private bool closed;
    public Guid Id { get; } = Guid.NewGuid();
    public RelayIdentity Identity { get; } = identity;

    public RelayCredential Read() { lock (gate) return credential; }

    public bool Renew(RelayCredential replacement)
    {
        lock (gate)
        {
            if (closed || replacement.ExpiresAt <= credential.ExpiresAt) return false;
            credential = replacement;
            return true;
        }
    }

    public RelayTokenState Observe(RelayCredential observed, RelayTokenState result, DateTimeOffset now, TimeSpan grace)
    {
        lock (gate)
        {
            if (!ReferenceEquals(observed, credential)) return RelayTokenState.Active;
            if (result == RelayTokenState.Active) credential = observed with { VerifiedAt = now };
            else if (result == RelayTokenState.Inactive) closed = true;
            else if (now - observed.ExpiresAt >= grace || now - observed.VerifiedAt >= grace) closed = true;
            return closed ? result : RelayTokenState.Active;
        }
    }

    public void Close() { lock (gate) closed = true; }
}
