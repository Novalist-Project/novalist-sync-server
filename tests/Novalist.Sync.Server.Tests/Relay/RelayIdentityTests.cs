using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Novalist.Sync.Server.Relay;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayIdentityTests
{
    [Theory]
    [InlineData("2001:db8:1234:5678::1", "2001:db8:1234:5678:ffff:eeee:dddd:cccc", true)]
    [InlineData("2001:db8:1234:5678::1", "2001:db8:1234:5679::1", false)]
    [InlineData("192.0.2.1", "::ffff:192.0.2.1", true)]
    [InlineData("192.0.2.1", "192.0.2.2", false)]
    public void AddressesShareQuotaOnlyWithinTheSamePrefix(string first, string second, bool shared)
    {
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { RequestsPerMinutePerIp = 1 }), TimeProvider.System);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(first);
        Assert.Equal(0, quotas.Request(RelayIdentity.From(context), false));
        context.Connection.RemoteIpAddress = IPAddress.Parse(second);
        Assert.Equal(shared ? 429 : 0, quotas.Request(RelayIdentity.From(context), false));
    }
}
