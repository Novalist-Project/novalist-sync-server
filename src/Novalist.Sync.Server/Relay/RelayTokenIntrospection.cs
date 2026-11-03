using System.Net.Http.Headers;
using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Novalist.Sync.Server.Relay;

internal sealed class RelayTokenIntrospection : IDisposable
{
    private readonly IHttpClientFactory clients;
    private readonly RelayIntrospectionOptions options;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim requests;

    public RelayTokenIntrospection(IHttpClientFactory clients, IOptions<RelayIntrospectionOptions> configuration, TimeProvider clock)
    {
        this.clients = clients;
        options = configuration.Value;
        this.clock = clock;
        requests = new(options.MaxConcurrentRequests);
    }

    public Task<RelayTokenState> CheckAsync(HttpContext context, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || authorization.Parameter is null)
            return Task.FromResult(RelayTokenState.Inactive);
        return CheckAsync(authorization.Parameter, RelayIdentity.From(context), ct);
    }

    public async Task<RelayTokenState> CheckAsync(string token, RelayIdentity identity, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds), clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await requests.WaitAsync(lifetime.Token);
            try { return await RequestAsync(token, identity, lifetime.Token); }
            finally { requests.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return RelayTokenState.Unavailable; }
        catch (HttpRequestException) { return RelayTokenState.Unavailable; }
        catch (IOException) { return RelayTokenState.Unavailable; }
        catch (JsonException) { return RelayTokenState.Unavailable; }
    }

    private async Task<RelayTokenState> RequestAsync(string token, RelayIdentity identity, CancellationToken ct)
    {
        using var client = clients.CreateClient("RelayIntrospection");
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        var credentials = WebUtility.UrlEncode(options.ClientId) + ":" + WebUtility.UrlEncode(options.ClientSecret);
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["token_type_hint"] = "access_token"
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > options.MaxResponseBytes)
            return RelayTokenState.Unavailable;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var limit = options.MaxResponseBytes + 1;
        var bytes = ArrayPool<byte>.Shared.Rent(limit);
        try
        {
            var count = await stream.ReadAtLeastAsync(bytes.AsMemory(0, limit), limit, false, ct);
            return count > options.MaxResponseBytes ? RelayTokenState.Unavailable
                : RelayIntrospectionResponse.Read(bytes.AsMemory(0, count), identity, clock.GetUtcNow());
        }
        finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
    }

    public void Dispose() => requests.Dispose();
}
