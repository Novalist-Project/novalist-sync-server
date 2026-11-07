using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Novalist.Sync.Server.Access;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// Starts only the production login registration on a directly controlled host. A failed
// WebApplicationFactory startup can dispose its provider before the deferred host observes
// the validation exception, which surfaces as ObjectDisposedException instead.
internal static class LoginConfigurationHost
{
    public static async Task<OptionsValidationException> StartFailureAsync(string environment, Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLoginAuthentication(builder.Configuration, builder.Environment);
        await using var app = builder.Build();
        return await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync(TestContext.Current.CancellationToken));
    }
}
