using Novalist.Sync.Server.Relay;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayAuthorizationTests
{
    [Fact]
    public void ProviderOutagesHaveABoundedGracePeriodThatSuccessfulChecksReset()
    {
        var now = DateTimeOffset.UtcNow;
        var credential = new RelayCredential("access", now.AddMinutes(5), now);
        var connection = new RelayAuthorization(new("address", "issuer", "subject"), credential);
        var grace = TimeSpan.FromMinutes(1);
        Assert.Equal(RelayTokenState.Active, connection.Observe(credential, RelayTokenState.Unavailable, now.AddSeconds(35), grace));
        Assert.Equal(RelayTokenState.Active, connection.Observe(credential, RelayTokenState.Active, now.AddSeconds(40), grace));
        credential = connection.Read();
        Assert.Equal(RelayTokenState.Active, connection.Observe(credential, RelayTokenState.Unavailable, now.AddSeconds(99), grace));
        Assert.Equal(RelayTokenState.Unavailable, connection.Observe(credential, RelayTokenState.Unavailable, now.AddSeconds(100), grace));
        Assert.False(connection.Renew(new("renewed", now.AddHours(1), now.AddSeconds(101))));
    }

    [Fact]
    public void InactiveTokensCloseImmediatelyButAnOldCheckCannotRevokeARenewedConnection()
    {
        var now = DateTimeOffset.UtcNow;
        var old = new RelayCredential("old", now.AddMinutes(5), now);
        var connection = new RelayAuthorization(new("address", "issuer", "subject"), old);
        Assert.True(connection.Renew(new("new", now.AddMinutes(10), now)));
        Assert.Equal(RelayTokenState.Active, connection.Observe(old, RelayTokenState.Inactive, now, TimeSpan.FromMinutes(5)));
        Assert.False(connection.Renew(old));
        Assert.False(connection.Renew(new("same-expiry", now.AddMinutes(10), now.AddSeconds(1))));
        Assert.Equal("new", connection.Read().Token);
        Assert.Equal(RelayTokenState.Inactive, connection.Observe(connection.Read(), RelayTokenState.Inactive, now, TimeSpan.FromMinutes(5)));
        Assert.False(connection.Renew(new("newer", now.AddHours(1), now)));
    }
}
