using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
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
        using var discovery = await client.GetAsync("/client-configuration", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, discovery.StatusCode);
    }

    [Fact]
    public async Task DesktopConfiguration_ProvidesPublicLoginSettingsWithoutAuthentication()
    {
        await using var server = new ProductionServer(database, "https://idp.test/realms/novalist", "novalist-sync", desktop: true);
        using var client = server.CreateClient();
        var configuration = await client.GetFromJsonAsync<NativeClientConfiguration>("/client-configuration", TestContext.Current.CancellationToken);
        Assert.Equal(new NativeClientConfiguration(1, "https://idp.test/realms/novalist", "novalist", "openid profile offline_access sync"), configuration);
    }

    [Theory]
    [InlineData("http://idp.example/realm", "openid profile")]
    [InlineData("https://idp.example/realm", "profile")]
    [InlineData("https://user@idp.example/realm", "openid")]
    [InlineData("https://idp.example/realm?query=1", "openid")]
    [InlineData("https://idp.example/realm#fragment", "openid")]
    [InlineData("http://127.0.0.1/realm", "openid")]
    [InlineData(" https://idp.example/realm ", "openid")]
    [InlineData("https://idp.example/realm", "openid \tprofile")]
    [InlineData("https://idp.example/realm", "openid profile\\invalid")]
    [InlineData("https://idp.example/realm", "openid profile\"invalid")]
    public async Task DesktopConfiguration_RejectsInvalidAuthorityOrScope(string authority, string scope)
    {
        await using var server = new ProductionServer(database, authority, "novalist-sync", desktop: true, scope: scope);
        using var client = server.CreateClient();
        using var response = await client.GetAsync(Protocol.ClientConfigurationPath, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task DesktopConfiguration_TrimsClientIdAndScope()
    {
        await using var server = new ProductionServer(database, "https://idp.test/realm", "novalist-sync",
            desktop: true, scope: " openid profile ", clientId: " novalist ");
        using var client = server.CreateClient();
        var configuration = await client.GetFromJsonAsync<NativeClientConfiguration>(Protocol.ClientConfigurationPath, TestContext.Current.CancellationToken);
        Assert.Equal(new NativeClientConfiguration(1, "https://idp.test/realm", "novalist", "openid profile"), configuration);
    }

    // The real server in the Production environment, configured only through settings,
    // the way an admin would through environment variables.
    private sealed class ProductionServer(DatabaseFixture database, string? authority, string? audience, bool desktop = false,
        string scope = "openid profile offline_access sync", string clientId = "novalist")
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Relay:Enabled", "false");
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
            if (desktop)
            {
                builder.UseSetting("Authentication:NativeClient:ClientId", clientId);
                builder.UseSetting("Authentication:NativeClient:Scope", scope);
                // Isolate discovery validation from JWT middleware's separate startup validation.
                builder.ConfigureTestServices(services => services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme, options => options.Configuration = new OpenIdConnectConfiguration()));
            }
        }
    }
}
