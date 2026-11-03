using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Novalist.Sync.Server.Relay;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedIntrospectionDoesNotSpendTheAccountUpgradeAllowance(bool unavailable)
    {
        var accepted = false;
        await using var server = new RelayTestServer(options => options.RequestsPerMinutePerAccount = 1,
            response: (_, _) => Task.FromResult(new HttpResponseMessage(unavailable && !accepted
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(accepted ? "{\"active\":true}" : "{\"active\":false}") }));
        await server.RejectedAsync("host", unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized);
        accepted = true;
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        await server.ReadIdAsync(control);
        await server.RejectedAsync("host", HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task RenewalWorksAfterTheAccountUpgradeAllowanceIsExhausted()
    {
        await using var server = new RelayTestServer(options => options.RequestsPerMinutePerAccount = 1);
        using var control = server.Socket(expires: DateTime.UtcNow.AddMinutes(2));
        await server.ConnectAsync(control, "host");
        await server.ReadIdAsync(control);
        var connection = Assert.Single(control.HttpResponseHeaders!["X-Novalist-Relay-Connection"]);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create("owner"));
        using var renewed = await client.PostAsync($"/relay/v1/connections/{connection}/token", null, server.Token);
        Assert.Equal(HttpStatusCode.NoContent, renewed.StatusCode);
        await server.RejectedAsync("host", HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task AuthenticationPolicyRemainsTheStartupPolicyAfterConfigurationChanges()
    {
        await using var server = new RelayTestServer(anonymous: true);
        server.Services.GetRequiredService<IConfiguration>()["Relay:RequireAuthentication"] = "true";
        using var control = server.Socket(capability: new string('a', 64));
        await server.ConnectAsync(control, "host");
        await server.ReadIdAsync(control);
        Assert.False(server.Services.GetRequiredService<RelayAuthenticationPolicy>().Required);
    }

    [Fact]
    public void CompletedRoutesRejectAttachmentWithoutChargingAnUpgrade()
    {
        var quotas = new RelayQuotas(Options.Create(new RelayOptions { RequestsPerMinute = 3 }), TimeProvider.System);
        var identity = new RelayIdentity("192.0.2.1", "issuer", "owner");
        Assert.True(quotas.TryReserve(false, out var reservation, out _, identity));
        using var reserved = reservation;
        var route = new RelayRoute(reservation);
        Assert.True(route.Claim());
        route.Complete();
        Assert.True(route.Done.IsCompletedSuccessfully);
        Assert.True(route.Host.IsCanceled);
        Assert.Equal(410, route.AdmitAttachment(quotas, identity));
        Assert.True(quotas.RequestUpgrade());
        Assert.True(quotas.RequestUpgrade());
        Assert.True(quotas.RequestUpgrade());
        Assert.False(quotas.RequestUpgrade());
    }

    [Fact]
    public void FractionalNumericDatePreservesItsExpiry()
    {
        var context = new DefaultHttpContext { User = TestTokens.Principal(("exp", "1800000000.25")) };
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000).AddMilliseconds(250), RelayLifetime.ExpiresAt(context));
    }
}
