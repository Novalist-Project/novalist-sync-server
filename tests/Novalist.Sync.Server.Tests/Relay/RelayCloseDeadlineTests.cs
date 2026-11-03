using System.Net.WebSockets;
using Novalist.Sync.Server.Relay;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayCloseDeadlineTests
{
    [Fact]
    public async Task CloseDeadlineCancelsABlockedCloseWriteAndTheOppositeReceive()
    {
        using var source = new BlockingCloseSocket(true);
        using var destination = new BlockingCloseSocket(false);
        await RelayPackets.TransferAsync(source, destination, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(destination.CloseWriteCancelled);
        Assert.Equal(WebSocketState.Aborted, source.State);
        Assert.Equal(WebSocketState.Aborted, destination.State);
    }

    private sealed class BlockingCloseSocket(bool receiveClose) : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;
        public bool CloseWriteCancelled { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { CloseWriteCancelled = true; throw; }
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!receiveClose) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(0, WebSocketMessageType.Close, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture only exchanges close frames.");
    }
}
