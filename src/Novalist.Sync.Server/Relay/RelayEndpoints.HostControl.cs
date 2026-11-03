using System.Net.WebSockets;

namespace Novalist.Sync.Server.Relay;

public static partial class RelayEndpoints
{
    private static async Task RunHostControlAsync(HttpContext context, WebSocket socket, RelaySession session, RelayLifetime lifetime)
    {
        using var io = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var send = SendRequestsAsync(socket, session, lifetime.Token, io.Token);
        var receive = socket.ReceiveAsync(new byte[1], io.Token);
        try { await Task.WhenAny(send, receive).WaitAsync(lifetime.Token); }
        finally
        {
            session.Close();
            await lifetime.CancelAsync();
            await CloseHostControlAsync(context, socket, (send, receive), lifetime.TokenState);
            await io.CancelAsync();
            try { await Task.WhenAll(send, receive); }
            catch (OperationCanceledException) when (io.IsCancellationRequested) { }
            catch (WebSocketException error) { ReportDisconnect(context, error); }
        }
    }

    private static async Task CloseHostControlAsync(HttpContext context, WebSocket socket, (Task Send, Task Receive) pending, RelayTokenState tokenState)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            try { await pending.Send.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
            var shutdown = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;
            var (status, reason) = tokenState switch
            {
                RelayTokenState.Inactive => (WebSocketCloseStatus.PolicyViolation, "Relay authorization ended."),
                RelayTokenState.Unavailable => (WebSocketCloseStatus.EndpointUnavailable, "Relay authorization service unavailable."),
                _ when shutdown => (WebSocketCloseStatus.EndpointUnavailable, "Relay shutting down."),
                _ => (WebSocketCloseStatus.NormalClosure, "Relay session closed.")
            };
            await socket.CloseOutputAsync(status, reason, deadline.Token);
            await pending.Receive.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        catch (WebSocketException error) { ReportDisconnect(context, error); }
    }
}
