using System.Net.WebSockets;
using System.Text;

namespace Novalist.Sync.Server.Relay;

public static partial class RelayEndpoints
{
    private static async Task HostAsync(HttpContext context, RelaySessions sessions, RelayQuotas quotas)
    {
        if (!await CanUpgradeAsync(context)) return;
        var anonymous = !RelayAuthentication.Required(context);
        var proof = anonymous ? RelayAuthentication.HostProof(context) : null;
        if (anonymous && proof is null) { context.Response.StatusCode = 400; return; }
        var owner = RelayIdentity.From(context);
        if (!quotas.TryReserve(true, out var reservation, out var status, owner)) { Reject(context, status); return; }
        using var reserved = reservation;
        var session = sessions.Create(owner, proof);
        await using var lifetime = new RelayLifetime(context, session.Stop.Token);
        WebSocket? socket = null;
        try
        {
            socket = await AcceptSocketAsync(context);
            if (socket is null) return;
            await SendIdAsync(socket, session.Id, lifetime.Token);
            await RunHostControlAsync(context, socket, session, lifetime);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (WebSocketException error) { ReportDisconnect(context, error); }
        finally
        {
            sessions.Remove(session);
            if (socket is not null)
            {
                if (socket.State != WebSocketState.Closed) socket.Abort();
                socket.Dispose();
            }
        }
    }

    private static async Task SendRequestsAsync(WebSocket socket, RelaySession session, CancellationToken waiting, CancellationToken sending)
    {
        await foreach (var id in session.Requests.Reader.ReadAllAsync(waiting)) await SendIdAsync(socket, id, sending);
    }

    private static Task SendIdAsync(WebSocket socket, Guid id, CancellationToken ct) =>
        socket.SendAsync(Encoding.ASCII.GetBytes(id.ToString("N")), WebSocketMessageType.Text, true, ct);
}
