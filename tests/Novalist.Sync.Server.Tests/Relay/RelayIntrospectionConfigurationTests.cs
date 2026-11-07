using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Novalist.Sync.Server.Relay;

namespace Novalist.Sync.Server.Tests.Relay;

// Uses a directly controlled host with the production AddRelay registration: a failed
// WebApplicationFactory startup can dispose its provider before the deferred host
// observes the validation exception, which surfaces as ObjectDisposedException.
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
        var settings = ValidIntrospection();
        settings["Relay:Introspection:" + setting] = value;
        Assert.Contains(setting, (await StartFailureAsync(Environments.Development, settings)).Message);
    }

    [Theory]
    [InlineData("Endpoint")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    public async Task AuthenticatedRelayRequiresIntrospectionConfiguration(string missing)
    {
        var settings = ValidIntrospection();
        settings["Relay:Introspection:" + missing] = "";
        Assert.Contains("Introspection", (await StartFailureAsync(Environments.Development, settings)).Message);
    }

    [Theory]
    [InlineData("Production", "http://127.0.0.1/introspect")]
    [InlineData("Development", "http://idp.example/introspect")]
    [InlineData("Development", "https://user:password@idp.example/introspect")]
    [InlineData("Development", "https://idp.example/introspect?secret=value")]
    [InlineData("Development", "https://idp.example/introspect#fragment")]
    public async Task UnsafeIntrospectionEndpointPreventsStartup(string environment, string endpoint)
    {
        var settings = ValidIntrospection();
        settings["Relay:Introspection:Endpoint"] = endpoint;
        Assert.Contains("Introspection", (await StartFailureAsync(environment, settings)).Message);
    }

    private static Dictionary<string, string?> ValidIntrospection() => new()
    {
        ["Relay:Enabled"] = "true",
        ["Relay:Introspection:Endpoint"] = "https://idp.test/introspect",
        ["Relay:Introspection:ClientId"] = "relay-test",
        ["Relay:Introspection:ClientSecret"] = "relay-test-secret"
    };

    private static async Task<OptionsValidationException> StartFailureAsync(string environment, Dictionary<string, string?> settings)
    {
        using var host = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) => services.AddRelay(context.Configuration))
            .Build();
        return await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }
}
