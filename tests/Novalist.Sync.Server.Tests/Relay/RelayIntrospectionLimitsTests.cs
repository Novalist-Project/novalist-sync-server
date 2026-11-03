using System.Net;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayIntrospectionLimitsTests
{
    [Fact]
    public async Task ConcurrentChecksNeverExceedConfiguredRequestLimit()
    {
        var active = 0;
        var counts = new ConcurrentBag<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new RelayTestServer(introspection: options => options.MaxConcurrentRequests = 2,
            response: async (_, ct) =>
            {
                var count = Interlocked.Increment(ref active);
                counts.Add(count);
                if (count == 2) entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(ct);
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"active\":true}") };
                }
                finally { Interlocked.Decrement(ref active); }
            });
        var sockets = Enumerable.Range(0, 6).Select(index => server.Socket("user-" + index)).ToArray();
        try
        {
            var joins = sockets.Select(socket => server.ConnectAsync(socket, "host")).ToArray();
            await entered.Task.WaitAsync(server.Token);
            Assert.Equal(2, counts.Max());
            release.SetResult();
            await Task.WhenAll(joins);
            Assert.Equal(2, counts.Max());
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderRedirectsAndOversizedStreamingResponsesFailClosed(bool oversized)
    {
        var redirected = 0;
        var requests = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var provider = builder.Build();
        provider.MapPost("/introspect", async context =>
        {
            Interlocked.Increment(ref requests);
            if (oversized) await context.Response.WriteAsync(new string(' ', 256) + "{\"active\":true}", context.RequestAborted);
            else context.Response.Redirect("/redirected", false, true);
        });
        provider.MapPost("/redirected", async context =>
        {
            Interlocked.Increment(ref redirected);
            await context.Response.WriteAsync("{\"active\":true}", context.RequestAborted);
        });
        await provider.StartAsync(TestContext.Current.CancellationToken);
        await using var server = new RelayTestServer(realIntrospection: true, introspection: options =>
        {
            options.Endpoint = provider.Urls.Single() + "/introspect";
            options.MaxResponseBytes = 128;
        });
        await server.RejectedAsync("host", HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1, Volatile.Read(ref requests));
        Assert.Equal(0, Volatile.Read(ref redirected));
    }
}
