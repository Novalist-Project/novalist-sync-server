using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayIntrospectionTests
{
    [Theory]
    [InlineData("{\"active\":false}", HttpStatusCode.Unauthorized)]
    [InlineData("{\"active\":true,\"sub\":\"someone-else\"}", HttpStatusCode.Unauthorized)]
    [InlineData("{\"active\":true,\"iss\":\"https://other.invalid\"}", HttpStatusCode.Unauthorized)]
    [InlineData("{\"active\":true,\"exp\":0}", HttpStatusCode.Unauthorized)]
    [InlineData("{\"active\":\"true\"}", HttpStatusCode.ServiceUnavailable)]
    [InlineData("{\"active\":true,\"exp\":\"bad\"}", HttpStatusCode.ServiceUnavailable)]
    [InlineData("{\"active\":true,\"sub\":42}", HttpStatusCode.ServiceUnavailable)]
    [InlineData("{\"active\":true,\"iss\":null}", HttpStatusCode.ServiceUnavailable)]
    [InlineData("{}", HttpStatusCode.ServiceUnavailable)]
    [InlineData("invalid", HttpStatusCode.ServiceUnavailable)]
    public async Task InactiveMismatchedOrMalformedIntrospectionCannotUpgrade(string body, HttpStatusCode expected)
    {
        await using var server = new RelayTestServer(response: (_, _) => Task.FromResult(Response(body)));
        await server.RejectedAsync("host", expected);
    }

    [Fact]
    public async Task DisabledHostAccountClosesTheSessionAndAllRoutes()
    {
        var active = 1;
        await using var server = new RelayTestServer(introspection: options => options.CheckIntervalSeconds = 5,
            response: (_, _) => Task.FromResult(Response(Volatile.Read(ref active) == 1 ? "{\"active\":true}" : "{\"active\":false}")));
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        var session = await server.ReadIdAsync(control);
        using var guest = server.Socket("guest");
        var joining = server.ConnectAsync(guest, $"sessions/{session}/guest");
        var route = await server.ReadIdAsync(control);
        using var host = server.Socket();
        await server.ConnectAsync(host, $"sessions/{session}/routes/{route}");
        await joining;
        Interlocked.Exchange(ref active, 0);
        await server.ClosedAsync(control);
        Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, control.CloseStatus);
        Assert.Equal("Relay authorization ended.", control.CloseStatusDescription);
        await server.ClosedAsync(guest);
        await server.ClosedAsync(host);
        await server.RejectedAsync("host", HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DisabledGuestLosesOnlyItsRoute()
    {
        var disabled = 0;
        await using var server = new RelayTestServer(introspection: options => options.CheckIntervalSeconds = 5,
            response: async (request, ct) =>
            {
                var token = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct))["token"].ToString();
                var subject = new JsonWebTokenHandler().ReadJsonWebToken(token).Subject;
                return Response(Volatile.Read(ref disabled) == 1 && subject == "guest" ? "{\"active\":false}" : "{\"active\":true}");
            });
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        var session = await server.ReadIdAsync(control);
        using var guest = server.Socket("guest");
        var joining = server.ConnectAsync(guest, $"sessions/{session}/guest");
        var route = await server.ReadIdAsync(control);
        using var host = server.Socket();
        await server.ConnectAsync(host, $"sessions/{session}/routes/{route}");
        await joining;
        Interlocked.Exchange(ref disabled, 1);
        await server.ClosedAsync(guest);
        await server.ClosedAsync(host);
        await server.RejectedAsync($"sessions/{session}/guest", HttpStatusCode.Unauthorized, "guest");
        using var next = server.Socket("other");
        var nextJoin = server.ConnectAsync(next, $"sessions/{session}/guest");
        var nextRoute = await server.ReadIdAsync(control);
        using var nextHost = server.Socket();
        await server.ConnectAsync(nextHost, $"sessions/{session}/routes/{nextRoute}");
        await nextJoin;
    }

    [Fact]
    public async Task ProviderTimeoutRejectsNewConnectionsAndStopsExistingSessions()
    {
        var unavailable = 0;
        await using var server = new RelayTestServer(introspection: options =>
        {
            options.CheckIntervalSeconds = 5;
            options.TimeoutSeconds = 1;
            options.GracePeriodSeconds = 0;
        }, response: async (_, ct) =>
        {
            if (Volatile.Read(ref unavailable) == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response("{\"active\":true}");
        });
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        await server.ReadIdAsync(control);
        Interlocked.Exchange(ref unavailable, 1);
        await server.ClosedAsync(control);
        Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, control.CloseStatus);
        Assert.Equal("Relay authorization service unavailable.", control.CloseStatusDescription);
        await server.RejectedAsync("host", HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task IntrospectionUsesAnAuthenticatedFormPostWithoutLoggingCredentials()
    {
        string? token = null;
        string? credentials = null;
        await using var server = new RelayTestServer(response: async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://idp.test/introspect", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            credentials = request.Headers.Authorization.Parameter;
            Assert.Equal("relay-test:relay-test-secret", Encoding.UTF8.GetString(Convert.FromBase64String(credentials!)));
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("access_token", form["token_type_hint"]);
            token = form["token"].ToString();
            Assert.Equal("owner", new JsonWebTokenHandler().ReadJsonWebToken(token).Subject);
            return Response("{\"active\":true}");
        });
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        await server.ReadIdAsync(control);
        var logs = string.Join("\n", server.Logs.Entries.Select(entry => entry.ToString()));
        Assert.DoesNotContain(Assert.IsType<string>(token), logs);
        Assert.DoesNotContain(Assert.IsType<string>(credentials), logs);
        Assert.DoesNotContain("relay-test-secret", logs);
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
