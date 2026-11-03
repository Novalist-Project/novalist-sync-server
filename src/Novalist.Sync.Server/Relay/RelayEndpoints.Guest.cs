using System.Net.WebSockets;
using Microsoft.Extensions.Options;

namespace Novalist.Sync.Server.Relay;

public static partial class RelayEndpoints
{
    private static async Task GuestAsync(HttpContext context, Guid sessionId, RelaySessions sessions, RelayQuotas quotas)
    {
        if (!await CanUpgradeAsync(context)) return;
        var session = sessions.Find(sessionId);
        if (session is null || session.Stop.IsCancellationRequested) { context.Response.StatusCode = 404; return; }
        if (!quotas.TryReserve(false, out var reservation, out var status, session.Owner, RelayIdentity.From(context)))
        { Reject(context, status); return; }
        using var reserved = reservation;
        if (!session.TryAddRoute(reserved, out var route, out status)) { Reject(context, status); return; }
        await ServeGuestAsync(context, session, route, reserved);
    }

    private static async Task ServeGuestAsync(HttpContext context, RelaySession session, RelayRoute route, RelayReservation reservation)
    {
        await using var lifetime = new RelayLifetime(context, session.Stop.Token, route.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(context.RequestServices.GetRequiredService<IOptions<RelayOptions>>().Value.RouteTimeoutSeconds));
        try
        {
            var host = await route.Host.WaitAsync(deadline.Token);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            using var guest = await AcceptSocketAsync(context);
            if (guest is null) return;
            await RelayPackets.TransferAsync(guest, host, lifetime.Token, reservation.ThrottleAsync);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested || route.Done.IsCompleted)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = 410;
        }
        catch (WebSocketException error) { ReportDisconnect(context, error); }
        finally { session.RemoveRoute(route); }
    }
}
