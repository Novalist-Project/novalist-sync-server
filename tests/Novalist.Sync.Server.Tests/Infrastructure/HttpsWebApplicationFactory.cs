using Microsoft.AspNetCore.Mvc.Testing;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// The server refuses plain HTTP outside local Development, and the in-memory test server
// takes the request scheme from the URI, so test clients address it over https.
public class HttpsWebApplicationFactory : WebApplicationFactory<Program>
{
    public static readonly Uri BaseAddress = new("https://localhost/");

    public HttpsWebApplicationFactory() => ClientOptions.BaseAddress = BaseAddress;
}
