using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Tests;

public class StartupTests(DatabaseFixture database)
{
    private const string Authority = "Authentication:Schemes:Bearer:Authority";
    private const string Audience = "Authentication:Schemes:Bearer:ValidAudiences:0";

    [Theory]
    [InlineData(null, null)]
    [InlineData("https://idp.test/realms/novalist", null)]
    [InlineData(null, "novalist-sync")]
    public async Task Production_WithoutLoginConfiguration_RefusesToStart(string? authority, string? audience)
    {
        await using var server = new ProductionServer(database, authority, audience);

        var ex = Assert.Throws<OptionsValidationException>(() => server.CreateClient());

        Assert.Contains(authority is null ? "Authority" : "ValidAudiences", ex.Message);
    }

    [Fact]
    public async Task Production_WithLoginConfiguration_Starts()
    {
        await using var server = new ProductionServer(database, "https://idp.test/realms/novalist", "novalist-sync");

        using var client = server.CreateClient();
        using var response = await client.PostAsync($"{Protocol.HubPath}/negotiate?negotiateVersion=1", null, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The real server in the Production environment, configured only through settings,
    // the way an admin would through environment variables.
    private sealed class ProductionServer(DatabaseFixture database, string? authority, string? audience)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Default", database.ConnectionString);
            // Omit missing settings: a null indexed entry still creates an audience
            // child, which JWT configuration binds as an empty audience value.
            if (authority is not null)
            {
                builder.UseSetting(Authority, authority);
            }

            if (audience is not null)
            {
                builder.UseSetting(Audience, audience);
            }
        }
    }
}
