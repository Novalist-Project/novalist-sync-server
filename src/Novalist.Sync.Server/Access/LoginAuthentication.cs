using Microsoft.AspNetCore.Authentication.JwtBearer;
using Novalist.Sync.Server.Relay;

namespace Novalist.Sync.Server.Access;

public static class LoginAuthentication
{
    // Bearer tokens from the configured OIDC issuer. Outside Development the issuer and
    // audience must be configured whenever authentication is required, or startup fails.
    public static IServiceCollection AddLoginAuthentication(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => options.MapInboundClaims = false);

        if (!environment.IsDevelopment() && RelayAuthentication.Required(configuration))
        {
            services
                .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Validate(
                    options => !string.IsNullOrEmpty(options.Authority),
                    "Authentication:Schemes:Bearer:Authority must be set in non-development environments.")
                .Validate(
                    options => !string.IsNullOrEmpty(options.Audience)
                               || options.TokenValidationParameters.ValidAudiences?.Any() == true,
                    "Authentication:Schemes:Bearer:ValidAudiences must be set in non-development environments.")
                .ValidateOnStart();
        }

        return services;
    }
}
