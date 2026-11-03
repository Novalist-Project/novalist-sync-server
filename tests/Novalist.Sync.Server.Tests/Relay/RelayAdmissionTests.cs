using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayAdmissionTests
{
    [Fact]
    public void AuthenticatedRelayRequiresRoomForBothAddressAndAccountQuotas()
    {
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Relay:Enabled", "true");
            builder.UseSetting("Relay:MaxQuotaEntries", "1");
            builder.UseSetting("Relay:Introspection:Endpoint", "https://idp.test/introspect");
            builder.UseSetting("Relay:Introspection:ClientId", "test");
            builder.UseSetting("Relay:Introspection:ClientSecret", "test");
        });
        Assert.Contains("two quota entries", Assert.Throws<OptionsValidationException>(() => server.CreateClient()).Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public async Task GlobalUpgradeAllowanceBelowThreeFailsValidation(string allowance)
    {
        await using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Relay:Enabled", "true");
            builder.UseSetting("Relay:RequireAuthentication", "false");
            builder.UseSetting("Relay:RequestsPerMinute", allowance);
        });
        Assert.Contains("RequestsPerMinute", Assert.Throws<OptionsValidationException>(() => server.CreateClient()).Message);
    }

    [Fact]
    public async Task UnauthenticatedTrafficCannotConsumeTheGlobalUpgradeAllowance()
    {
        await using var server = new RelayTestServer(options => options.RequestsPerMinute = 3);
        await server.RejectedAsync("host", HttpStatusCode.Unauthorized, null);
        await server.RejectedAsync("host", HttpStatusCode.Unauthorized, null);
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        await server.ReadIdAsync(control);
        using var second = server.Socket("second");
        await server.ConnectAsync(second, "host");
        await server.ReadIdAsync(second);
        using var third = server.Socket("third");
        await server.ConnectAsync(third, "host");
        await server.ReadIdAsync(third);
        await server.RejectedAsync("host", HttpStatusCode.TooManyRequests, "other");
    }

    [Fact]
    public async Task OrdinaryHttpRequestsDoNotConsumeAccountOrGlobalUpgradeAllowances()
    {
        await using var server = new RelayTestServer(options =>
        {
            options.RequestsPerMinute = 3;
            options.RequestsPerMinutePerAccount = 1;
        });
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create("owner"));
        foreach (var _ in Enumerable.Range(0, 3))
        {
            using var response = await client.GetAsync("/relay/v1/host", server.Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        await server.ReadIdAsync(control);
    }

    [Fact]
    public async Task InvalidRoutesDoNotConsumeTheGlobalUpgradeAllowance()
    {
        await using var server = new RelayTestServer(options =>
        {
            options.RequestsPerMinute = 3;
            options.RequestsPerMinutePerAccount = 1;
        });
        await server.RejectedAsync($"sessions/{Guid.NewGuid()}/guest", HttpStatusCode.NotFound);
        await server.RejectedAsync($"sessions/{Guid.NewGuid()}/guest", HttpStatusCode.NotFound);
        await server.RejectedAsync($"sessions/{Guid.NewGuid()}/guest", HttpStatusCode.NotFound);
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        await server.ReadIdAsync(control);
    }

    [Theory]
    [InlineData("192.0.2.1", 429)]
    [InlineData("192.0.2.2", 503)]
    public async Task PreauthenticationIpLimitsRejectOrdinaryHttpBeforeUpgradeValidation(string secondAddress, int expected)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Relay:Enabled", "true");
            builder.UseSetting("Relay:RequireAuthentication", "false");
            builder.UseSetting("Relay:RequestsPerMinutePerIp", "1");
            builder.UseSetting("Relay:MaxQuotaEntries", "1");
        });
        using var client = factory.CreateClient();
        await SendAsync("192.0.2.1", 400);
        await SendAsync(secondAddress, expected);

        async Task SendAsync(string address, int status)
        {
            var result = await factory.Server.SendAsync(context =>
            {
                context.Request.Method = "GET";
                context.Request.Path = "/relay/v1/host";
                context.Request.Scheme = "https";
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            }, TestContext.Current.CancellationToken);
            Assert.Equal(status, result.Response.StatusCode);
            Assert.False(result.Response.Headers.ContainsKey("Retry-After"));
        }
    }
}
