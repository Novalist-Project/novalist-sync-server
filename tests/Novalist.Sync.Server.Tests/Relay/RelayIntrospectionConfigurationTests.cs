using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayIntrospectionConfigurationTests
{
    [Theory]
    [InlineData("CheckIntervalSeconds", "0.01")]
    [InlineData("CheckIntervalSeconds", "4")]
    [InlineData("CheckIntervalSeconds", "4.99")]
    [InlineData("TimeoutSeconds", "0.01")]
    [InlineData("TimeoutSeconds", "0.5")]
    [InlineData("TimeoutSeconds", "0.99")]
    public async Task ImpracticallyShortIntrospectionTimersPreventStartup(string setting, string value)
    {
        await using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Relay:Enabled", "true");
            builder.UseSetting("Relay:Introspection:Endpoint", "https://idp.test/introspect");
            builder.UseSetting("Relay:Introspection:ClientId", "relay-test");
            builder.UseSetting("Relay:Introspection:ClientSecret", "relay-test-secret");
            builder.UseSetting("Relay:Introspection:" + setting, value);
        });
        Assert.Contains(setting, Assert.Throws<OptionsValidationException>(() => server.CreateClient()).Message);
    }

    [Theory]
    [InlineData("Endpoint")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    public async Task AuthenticatedRelayRequiresIntrospectionConfiguration(string missing)
    {
        await using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Relay:Enabled", "true");
            builder.UseSetting("Relay:Introspection:Endpoint", "https://idp.test/introspect");
            builder.UseSetting("Relay:Introspection:ClientId", "relay-test");
            builder.UseSetting("Relay:Introspection:ClientSecret", "relay-test-secret");
            builder.UseSetting("Relay:Introspection:" + missing, "");
        });
        Assert.Contains("Introspection", Assert.Throws<OptionsValidationException>(() => server.CreateClient()).Message);
    }

    [Theory]
    [InlineData("Production", "http://127.0.0.1/introspect")]
    [InlineData("Development", "http://idp.example/introspect")]
    [InlineData("Development", "https://user:password@idp.example/introspect")]
    [InlineData("Development", "https://idp.example/introspect?secret=value")]
    [InlineData("Development", "https://idp.example/introspect#fragment")]
    public async Task UnsafeIntrospectionEndpointPreventsStartup(string environment, string endpoint)
    {
        await using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Relay:Enabled", "true");
            builder.UseSetting("Authentication:Schemes:Bearer:Authority", "https://idp.example");
            builder.UseSetting("Authentication:Schemes:Bearer:ValidAudiences:0", "novalist");
            builder.UseSetting("Relay:Introspection:Endpoint", endpoint);
            builder.UseSetting("Relay:Introspection:ClientId", "relay-test");
            builder.UseSetting("Relay:Introspection:ClientSecret", "relay-test-secret");
        });
        Assert.Contains("Introspection", Assert.Throws<OptionsValidationException>(() => server.CreateClient()).Message);
    }
}
