using System.Net;

namespace Novalist.Sync.Server.Tests.Relay;

internal sealed class RelayIntrospectionHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        response is null ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"active\":true}") })
            : response(request, cancellationToken);
}
