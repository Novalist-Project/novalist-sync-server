using System.Net.WebSockets;

namespace Novalist.Sync.Server.Relay;

public static partial class RelayEndpoints
{
    private static async Task AttachAsync(HttpContext context, Guid sessionId, Guid routeId, RelaySessions sessions, RelayQuotas quotas)
    {
        if (!await CanUpgradeAsync(context)) return;
        var session = sessions.Find(sessionId);
        if (session is null || session.Stop.IsCancellationRequested) { context.Response.StatusCode = 404; return; }
        if (!RelayAuthentication.Owns(session, context)) { context.Response.StatusCode = 403; return; }
        var route = session.ClaimRoute(routeId);
        if (route is null) { context.Response.StatusCode = 404; return; }
        var status = route.AdmitAttachment(quotas, RelayIdentity.From(context));
        if (status != 0)
        {
            route.ReleaseClaim();
            Reject(context, status);
            return;
        }
        await using var lifetime = new RelayLifetime(context, session.Stop.Token);
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try { if (route.Attach(socket)) await route.Done.WaitAsync(lifetime.Token); }
            finally { route.Complete(); socket.Abort(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (WebSocketException error) { ReportDisconnect(context, error); }
        finally { route.Complete(); }
    }
}
