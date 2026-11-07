using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Hubs;

public sealed class PresenceDeliveryTests(DatabaseFixture database)
{
    // These tests register more simultaneous connections than one account may normally open.
    private static readonly Dictionary<string, string> ManyConnections = new()
    {
        ["Transport:MaxConnectionsPerAccount"] = "1024",
        ["Transport:MaxConnectionsPerIp"] = "1024"
    };

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task MembershipAndEnvelopeFailuresDoNotRevealThePresenceCapacity(bool member, bool validEnvelope, bool currentEpoch)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database, settings: ManyConnections);
        var owner = await database.CreateUserAsync();
        var sender = member ? owner : await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(sender.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        await using var hub = server.CreateHubConnection(TestTokens.Create(sender.Subject), device);
        await hub.StartAsync(ct);
        var registry = server.Services.GetRequiredService<ConnectionRegistry>();
        for (var index = 0; index <= PresenceAccess.MaximumRecipients; index++)
        {
            var recipient = new FakeHubCallerContext();
            registry.Add(recipient, owner.Id, device);
            registry.JoinProject(recipient.ConnectionId, project);
        }
        var envelope = TestData.Envelope(project, device, 1, MsgType.Presence);
        if (!validEnvelope) envelope = envelope with { Nonce = new byte[23] };
        if (!currentEpoch) envelope = envelope with { Header = (EnvelopeHeader.Parse(envelope.Header) with { Epoch = 1 }).ToBytes() };
        var error = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("PublishPresence", envelope, ct));
        Assert.Contains(!member ? "not a member" : !validEnvelope ? "Invalid presence envelope" : "epoch is no longer current", error.Message);
        Assert.DoesNotContain("Presence is limited to", error.Message);
    }

    [Fact]
    public async Task PresenceCountsEveryConnectionAgainstItsFanoutLimitEvenForTheSameDevice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database, settings: ManyConnections);
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await using var hub = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        await hub.StartAsync(ct);
        var registry = server.Services.GetRequiredService<ConnectionRegistry>();
        var contexts = Enumerable.Range(0, PresenceAccess.MaximumRecipients + 1).Select(_ => new FakeHubCallerContext()).ToArray();
        foreach (var context in contexts)
        {
            registry.Add(context, user.Id, device);
            registry.JoinProject(context.ConnectionId, project);
        }
        var envelope = TestData.Envelope(project, device, 1, MsgType.Presence);
        var error = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("PublishPresence", envelope, ct));
        Assert.Contains("256 active recipients", error.Message);
        Assert.Equal(1, await hub.InvokeAsync<long>("Push", TestData.Envelope(project, device, 2), ct));
        registry.Remove(contexts[^1].ConnectionId);
        await hub.InvokeAsync("PublishPresence", envelope, ct);
    }

    [Fact]
    public async Task SlowPresenceReceivesSignalRCancellationAndDoesNotPreventTheNextEdit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var lifetime = new SlowPresenceLifetimeManager();
        await using var server = new SyncServer(database, lifetime: lifetime, settings: ManyConnections);
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var guestDevice = await database.CreateDeviceAsync(guest.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (guest.Id, MembershipRole.Contributor));
        await using var host = server.CreateHubConnection(TestTokens.Create(owner.Subject), device);
        await using var recipient = server.CreateHubConnection(TestTokens.Create(guest.Subject), guestDevice);
        await host.StartAsync(ct);
        await recipient.StartAsync(ct);
        await recipient.InvokeAsync("JoinProject", project, ct);
        var error = await Assert.ThrowsAsync<HubException>(() => host.InvokeAsync("PublishPresence",
            TestData.Envelope(project, device, 1, MsgType.Presence), ct).WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Contains("Presence delivery timed out.", error.Message);
        Assert.True(lifetime.Cancelled);
        Assert.Equal([recipient.ConnectionId!], lifetime.Recipients);
        Assert.Equal(1, await host.InvokeAsync<long>("Push", TestData.Envelope(project, device, 2), ct));
    }

    private sealed class SlowPresenceLifetimeManager() : DefaultHubLifetimeManager<SyncHub>(NullLogger<DefaultHubLifetimeManager<SyncHub>>.Instance), IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public bool Cancelled { get; private set; }
        public IReadOnlyList<string> Recipients { get; private set; } = [];

        public override async Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args,
            CancellationToken cancellationToken = default)
        {
            if (methodName != nameof(ISyncClient.ReceivePresence))
            {
                await base.SendConnectionsAsync(connectionIds, methodName, args, cancellationToken);
                return;
            }
            Assert.True(cancellationToken.CanBeCanceled);
            Recipients = connectionIds;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token); }
            finally { Cancelled = cancellationToken.IsCancellationRequested; }
        }

        public void Dispose() { stopping.Cancel(); stopping.Dispose(); }
    }
}
