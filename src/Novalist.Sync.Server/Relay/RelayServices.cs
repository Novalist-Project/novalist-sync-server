namespace Novalist.Sync.Server.Relay;

public static class RelayServices
{
    public static IServiceCollection AddRelay(this IServiceCollection services, IConfiguration configuration)
    {
        var authenticationRequired = RelayAuthentication.Required(configuration);
        services.AddOptions<RelayOptions>().Bind(configuration.GetSection("Relay"))
            .ValidateDataAnnotations()
            .Validate(options => !authenticationRequired || options.MaxQuotaEntries >= 2,
                "Authenticated relay requires at least two quota entries for an IP and account.")
            .ValidateOnStart();
        services.AddOptions<RelayIntrospectionOptions>().Bind(configuration.GetSection("Relay:Introspection"))
            .ValidateDataAnnotations()
            .Validate<IHostEnvironment>((options, environment) => !authenticationRequired || options.Valid(environment),
                "Authenticated relay requires Relay:Introspection:Endpoint, ClientId and ClientSecret. Use HTTPS (loopback HTTP is allowed in Development).")
            .ValidateOnStart();
        services.AddHttpClient("RelayIntrospection", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxResponseHeadersLength = 16
            })
            .RedactLoggedHeaders(_ => true);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RelayTokenIntrospection>();
        services.AddSingleton<RelayAuthorizations>();
        services.AddSingleton<RelayQuotas>();
        services.AddSingleton<RelaySessions>();
        return services.AddRelayTransportSecurity(configuration);
    }
}
