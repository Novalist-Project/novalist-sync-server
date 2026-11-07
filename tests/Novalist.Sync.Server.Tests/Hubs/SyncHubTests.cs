using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Hubs;

public sealed class SyncHubTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly SyncServer _server = new(database);
    private readonly List<TestClient> _clients = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Connecting ---------------------------------------------------------------

    [Fact]
    public async Task Connect_WithValidTokenAndDevice_Succeeds()
    {
        var client = await ConnectAsync();

        Assert.Equal(HubConnectionState.Connected, client.Connection.State);
    }

    [Fact]
    public async Task Connect_WithoutToken_IsUnauthorized()
    {
        var user = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id);
        await using var connection = _server.CreateHubConnection(token: null, deviceId);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Connect_WithTokenFromUntrustedKey_IsUnauthorized()
    {
        var user = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id);
        var forged = TestTokens.Create(user.Subject, signingKey: new SymmetricSecurityKey(new byte[32]));
        await using var connection = _server.CreateHubConnection(forged, deviceId);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Connect_WithExpiredToken_IsUnauthorized()
    {
        var user = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id);
        var expired = TestTokens.Create(user.Subject, expires: DateTime.UtcNow.AddMinutes(-10));
        await using var connection = _server.CreateHubConnection(expired, deviceId);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Connect_WithTokenFromOtherIssuer_IsUnauthorized()
    {
        var user = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id);
        var token = TestTokens.Create(user.Subject, issuer: "https://other-idp.test");
        await using var connection = _server.CreateHubConnection(token, deviceId);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Connect_WithoutDeviceId_IsRejected()
    {
        var user = await database.CreateUserAsync();

        var error = await ConnectExpectingRejectionAsync(TestTokens.Create(user.Subject), deviceId: null);

        Assert.Contains(Protocol.DeviceIdParameter, error);
    }

    [Fact]
    public async Task Connect_WithUnknownDevice_IsRejected()
    {
        var user = await database.CreateUserAsync();

        var error = await ConnectExpectingRejectionAsync(TestTokens.Create(user.Subject), Guid.NewGuid());

        Assert.Contains("not an active device", error);
    }

    [Fact]
    public async Task Connect_WithRevokedDevice_IsRejected()
    {
        var user = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id, revoked: true);

        var error = await ConnectExpectingRejectionAsync(TestTokens.Create(user.Subject), deviceId);

        Assert.Contains("not an active device", error);
    }

    [Fact]
    public async Task Connect_WithDeviceOfAnotherUser_IsRejected()
    {
        var owner = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(owner.Id);
        var other = await database.CreateUserAsync();

        var error = await ConnectExpectingRejectionAsync(TestTokens.Create(other.Subject), deviceId);

        Assert.Contains("not an active device", error);
    }

    [Fact]
    public async Task Connect_FirstLogin_CreatesUserFromToken()
    {
        var subject = Guid.NewGuid().ToString();
        await using var connection = _server.CreateHubConnection(TestTokens.Create(subject), Guid.NewGuid());

        // The device is unknown, so the connection is refused, but the user exists afterwards.
        await ConnectExpectingRejectionAsync(connection);

        await using var db = database.CreateContext();
        Assert.True(await db.Users.AnyAsync(u => u.Issuer == TestTokens.Issuer && u.Subject == subject, Ct));
    }

    // --- Projects -----------------------------------------------------------------

    [Fact]
    public async Task CreateProject_MakesCallerOwner()
    {
        var client = await ConnectAsync();
        var projectId = Guid.NewGuid();

        await client.CreateProjectAsync(projectId);

        await using var db = database.CreateContext();
        var membership = await db.Memberships.SingleAsync(m => m.ProjectId == projectId, Ct);
        Assert.Equal(client.UserId, membership.UserId);
        Assert.Equal(MembershipRole.Owner, membership.Role);
    }

    [Fact]
    public async Task CreateProject_WithIdOfAnotherUsersProject_IsRejected()
    {
        var owner = await ConnectAsync();
        var other = await ConnectAsync();
        var projectId = Guid.NewGuid();
        await owner.CreateProjectAsync(projectId);

        var ex = await Assert.ThrowsAsync<HubException>(() => other.CreateProjectAsync(projectId));

        Assert.Contains("already taken", ex.Message);
    }

    [Fact]
    public async Task JoinProject_AsNonMember_IsRejected()
    {
        var owner = await database.CreateUserAsync();
        var projectId = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var outsider = await ConnectAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() => outsider.JoinAsync(projectId));

        Assert.Contains("Not a member", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Push and live updates ----------------------------------------------------

    [Fact]
    public async Task Push_IsDeliveredToOtherMembersWithUnchangedBytes()
    {
        var alice = await ConnectAsync();
        var bob = await ConnectAsync();
        var projectId = await database.CreateProjectAsync(
            (alice.UserId, MembershipRole.Owner),
            (bob.UserId, MembershipRole.Contributor));
        await alice.JoinAsync(projectId);
        await bob.JoinAsync(projectId);
        var envelope = TestData.Envelope(projectId, alice.DeviceId, senderSeq: 1);

        var serverSeq = await alice.PushAsync(envelope);
        var received = await bob.NextUpdateAsync();

        Assert.Equal(1, serverSeq);
        Assert.Equal(serverSeq, received.ServerSeq);
        Assert.Equal(envelope.Header, received.Envelope.Header);
        Assert.Equal(envelope.Nonce, received.Envelope.Nonce);
        Assert.Equal(envelope.Ciphertext, received.Envelope.Ciphertext);
        Assert.Equal(envelope.Signature, received.Envelope.Signature);
        Assert.False(alice.TryTakeUpdate(out _), "The sender must not get its own update back.");
    }

    [Fact]
    public async Task Push_ReachesOtherDevicesOfTheSameUser()
    {
        var user = await database.CreateUserAsync();
        var laptop = await ConnectAsync(user);
        var desktop = await ConnectAsync(user);
        var projectId = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await desktop.JoinAsync(projectId);

        var serverSeq = await laptop.PushAsync(TestData.Envelope(projectId, laptop.DeviceId, senderSeq: 1));

        Assert.Equal(serverSeq, (await desktop.NextUpdateAsync()).ServerSeq);
    }

    [Fact]
    public async Task Push_IsNotDeliveredToConnectionsThatDidNotJoinOrLeft()
    {
        var alice = await ConnectAsync();
        var bob = await ConnectAsync();
        var carol = await ConnectAsync();
        var projectId = await database.CreateProjectAsync(
            (alice.UserId, MembershipRole.Owner),
            (bob.UserId, MembershipRole.Contributor),
            (carol.UserId, MembershipRole.Contributor));
        var otherProjectId = await database.CreateProjectAsync((bob.UserId, MembershipRole.Owner));
        var observer = await ConnectAsync(await database.CreateUserAsync());
        await AddMemberAsync(projectId, observer.UserId, MembershipRole.Viewer);
        await observer.JoinAsync(projectId);
        await bob.JoinAsync(otherProjectId);
        await carol.JoinAsync(projectId);
        await carol.LeaveAsync(projectId);

        await alice.PushAsync(TestData.Envelope(projectId, alice.DeviceId, senderSeq: 1));

        await observer.NextUpdateAsync();
        Assert.False(bob.TryTakeUpdate(out _), "Bob only joined another project.");
        Assert.False(carol.TryTakeUpdate(out _), "Carol left the project.");
    }

    [Fact]
    public async Task Push_AsViewer_IsRejected()
    {
        var owner = await database.CreateUserAsync();
        var viewer = await ConnectAsync();
        var projectId = await database.CreateProjectAsync(
            (owner.Id, MembershipRole.Owner),
            (viewer.UserId, MembershipRole.Viewer));

        var ex = await Assert.ThrowsAsync<HubException>(
            () => viewer.PushAsync(TestData.Envelope(projectId, viewer.DeviceId, senderSeq: 1)));

        Assert.Contains("write access", ex.Message);
    }

    [Fact]
    public async Task Push_AsNonMember_IsRejected()
    {
        var owner = await database.CreateUserAsync();
        var projectId = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var outsider = await ConnectAsync();

        var ex = await Assert.ThrowsAsync<HubException>(
            () => outsider.PushAsync(TestData.Envelope(projectId, outsider.DeviceId, senderSeq: 1)));

        Assert.Contains("write access", ex.Message);
    }

    [Fact]
    public async Task Push_InTheNameOfAnotherDevice_IsRejected()
    {
        var user = await database.CreateUserAsync();
        var laptop = await ConnectAsync(user);
        var desktopId = await database.CreateDeviceAsync(user.Id);
        var projectId = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));

        var ex = await Assert.ThrowsAsync<HubException>(
            () => laptop.PushAsync(TestData.Envelope(projectId, desktopId, senderSeq: 1)));

        Assert.Contains(desktopId.ToString(), ex.Message);
    }

    [Fact]
    public async Task Push_WithUnsupportedProtoVersion_IsRejectedWithClearError()
    {
        var client = await ConnectAsync();
        var projectId = await database.CreateProjectAsync((client.UserId, MembershipRole.Owner));
        var envelope = TestData.Envelope(projectId, client.DeviceId, senderSeq: 1);
        envelope.Header[0] = Protocol.Version + 1;

        var ex = await Assert.ThrowsAsync<HubException>(() => client.PushAsync(envelope));

        Assert.Contains("Unsupported protocol version", ex.Message);
    }

    [Fact]
    public async Task Push_WithGarbageHeader_IsRejected()
    {
        var client = await ConnectAsync();
        var projectId = await database.CreateProjectAsync((client.UserId, MembershipRole.Owner));
        var envelope = TestData.Envelope(projectId, client.DeviceId, senderSeq: 1) with { Header = [Protocol.Version, 1, 2, 3] };

        await Assert.ThrowsAsync<HubException>(() => client.PushAsync(envelope));
    }

    // --- Catch-up -----------------------------------------------------------------

    [Fact]
    public async Task CatchUp_StreamsUpdatesAfterTheGivenServerSeq()
    {
        var writer = await ConnectAsync();
        var reader = await ConnectAsync();
        var projectId = await database.CreateProjectAsync(
            (writer.UserId, MembershipRole.Owner),
            (reader.UserId, MembershipRole.Viewer));
        var sent = new List<Envelope>();
        for (var i = 1UL; i <= 5; i++)
        {
            var envelope = TestData.Envelope(projectId, writer.DeviceId, i);
            sent.Add(envelope);
            await writer.PushAsync(envelope);
        }

        var updates = await reader.CatchUpAsync(projectId, afterServerSeq: 2);

        Assert.Equal([3L, 4L, 5L], updates.Select(u => u.ServerSeq));
        Assert.Equal(sent[2].Ciphertext, updates[0].Envelope.Ciphertext);
        Assert.Equal(sent[4].Signature, updates[2].Envelope.Signature);
    }

    [Fact]
    public async Task CatchUp_AsNonMember_IsRejectedWithClearError()
    {
        var owner = await database.CreateUserAsync();
        var projectId = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var outsider = await ConnectAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() => outsider.CatchUpAsync(projectId, afterServerSeq: 0));

        Assert.Contains("Not a member", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Revocation and removal (security rule 4) ---------------------------------

    [Fact]
    public async Task RevokedDevice_IsRejectedOnEveryOperation()
    {
        var client = await ConnectAsync();
        var projectId = await database.CreateProjectAsync((client.UserId, MembershipRole.Owner));
        await client.JoinAsync(projectId);

        // Revoked only in the database, without closing the connection: every call must check.
        await using (var db = database.CreateContext())
        {
            await db.Devices
                .Where(d => d.Id == client.DeviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow), Ct);
        }

        await Assert.ThrowsAsync<HubException>(() => client.CreateProjectAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<HubException>(() => client.JoinAsync(projectId));
        await Assert.ThrowsAsync<HubException>(() => client.PushAsync(TestData.Envelope(projectId, client.DeviceId, senderSeq: 1)));
        await Assert.ThrowsAsync<HubException>(() => client.Connection.InvokeAsync("PublishPresence",
            TestData.Envelope(projectId, client.DeviceId, 2, MsgType.Presence), Ct));
        await Assert.ThrowsAsync<HubException>(() => client.CatchUpAsync(projectId, afterServerSeq: 0));
    }

    [Fact]
    public async Task RevokeDevice_ClosesItsOpenConnectionsAndBlocksReconnecting()
    {
        var user = await database.CreateUserAsync();
        var stolenLaptop = await ConnectAsync(user);
        var desktop = await ConnectAsync(user);
        var projectId = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await stolenLaptop.JoinAsync(projectId);

        await RevokeDeviceAsync(user.Id, stolenLaptop.DeviceId);

        await stolenLaptop.Closed;
        Assert.Equal(HubConnectionState.Disconnected, stolenLaptop.Connection.State);
        Assert.Equal(HubConnectionState.Connected, desktop.Connection.State);

        await ConnectExpectingRejectionAsync(TestTokens.Create(user.Subject), stolenLaptop.DeviceId);
    }

    [Fact]
    public async Task RemoveMember_StopsLiveUpdatesAndRejectsEveryOperationOnTheOpenConnection()
    {
        var owner = await ConnectAsync();
        var removed = await ConnectAsync();
        var projectId = await database.CreateProjectAsync(
            (owner.UserId, MembershipRole.Owner),
            (removed.UserId, MembershipRole.Contributor));
        var otherProjectId = await database.CreateProjectAsync((removed.UserId, MembershipRole.Owner));
        var observer = await ConnectAsync();
        await AddMemberAsync(projectId, observer.UserId, MembershipRole.Viewer);
        await removed.JoinAsync(projectId);
        await removed.JoinAsync(otherProjectId);
        await observer.JoinAsync(projectId);

        await RemoveMemberAsync(owner.UserId, projectId, removed.UserId);
        await owner.PushAsync(TestData.Envelope(projectId, owner.DeviceId, senderSeq: 1));

        await observer.NextUpdateAsync();
        Assert.False(removed.TryTakeUpdate(out _), "A removed member must not receive live updates.");
        await Assert.ThrowsAsync<HubException>(() => removed.JoinAsync(projectId));
        await Assert.ThrowsAsync<HubException>(() => removed.PushAsync(TestData.Envelope(projectId, removed.DeviceId, senderSeq: 1)));
        await Assert.ThrowsAsync<HubException>(() => removed.CatchUpAsync(projectId, afterServerSeq: 0));

        // The connection itself stays usable for the user's other projects.
        Assert.Equal(1, await removed.PushAsync(TestData.Envelope(otherProjectId, removed.DeviceId, senderSeq: 2)));
    }

    // --- Logging (security rule 3) ------------------------------------------------

    [Fact]
    public async Task Logs_NeverContainPayloadBytes()
    {
        var alice = await ConnectAsync();
        var bob = await ConnectAsync();
        var projectId = await database.CreateProjectAsync(
            (alice.UserId, MembershipRole.Owner),
            (bob.UserId, MembershipRole.Viewer));
        await bob.JoinAsync(projectId);
        var envelopes = new List<Envelope>();

        for (var i = 1UL; i <= 3; i++)
        {
            var envelope = TestData.Envelope(projectId, alice.DeviceId, i);
            envelopes.Add(envelope);
            await alice.PushAsync(envelope);
            await bob.NextUpdateAsync();
        }

        await bob.CatchUpAsync(projectId, afterServerSeq: 0);

        // Failing paths log too: a rejected push and a retried duplicate with other bytes.
        var rejected = TestData.Envelope(projectId, bob.DeviceId, senderSeq: 1);
        envelopes.Add(rejected);
        await Assert.ThrowsAsync<HubException>(() => bob.PushAsync(rejected));
        var conflicting = TestData.Envelope(projectId, alice.DeviceId, senderSeq: 1);
        envelopes.Add(conflicting);
        await Assert.ThrowsAsync<HubException>(() => alice.PushAsync(conflicting));

        var payloads = envelopes
            .SelectMany(e => new[] { e.Nonce, e.Ciphertext, e.Signature })
            .SelectMany(bytes => new[] { Convert.ToHexString(bytes), Convert.ToHexStringLower(bytes), Convert.ToBase64String(bytes) })
            .ToList();

        // Guards against a capture that silently sees nothing: the hub's own push logs must be there.
        Assert.Contains(_server.Logs.Entries, e => e.Category == typeof(SyncHub).FullName && e.Values.Contains(projectId));
        Assert.Contains(_server.Logs.Entries, e => e.ScopeValues.Count > 0);
        foreach (var entry in _server.Logs.Entries)
        {
            var text = entry.Message + entry.Exception + entry.ScopeText;
            Assert.DoesNotContain(payloads, payload => text.Contains(payload, StringComparison.Ordinal));
            Assert.DoesNotContain(
                entry.Values.Concat(entry.ScopeValues),
                value => value is byte[] or Envelope or SequencedEnvelope);
        }
    }

    // --- Helpers ------------------------------------------------------------------

    [Fact]
    public async Task Presence_ReachesConnectedMembers_WithoutEnteringCatchUp()
    {
        var owner = await ConnectAsync();
        var guest = await ConnectAsync();
        var project = await database.CreateProjectAsync((owner.UserId, MembershipRole.Owner),
            (guest.UserId, MembershipRole.Contributor));
        await owner.JoinAsync(project);
        await guest.JoinAsync(project);
        var received = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = guest.Connection.On<Envelope>("ReceivePresence", value => received.TrySetResult(value));
        var presence = TestData.Envelope(project, owner.DeviceId, 1, MsgType.Presence);

        await owner.Connection.InvokeAsync("PublishPresence", presence, Ct);

        var relayed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(presence.Ciphertext, relayed.Ciphertext);
        Assert.Equal(presence.Header, relayed.Header);
        Assert.Equal(presence.Nonce, relayed.Nonce);
        Assert.Equal(presence.Signature, relayed.Signature);
        Assert.Empty(await guest.CatchUpAsync(project, 0));
        Assert.Equal(1, await owner.PushAsync(TestData.Envelope(project, owner.DeviceId, 2)));
    }

    [Fact]
    public async Task Presence_RejectsOtherDevicesAndInvalidEnvelopes_AndStopsOnRemoval()
    {
        var owner = await ConnectAsync();
        var guest = await ConnectAsync();
        var project = await database.CreateProjectAsync((owner.UserId, MembershipRole.Owner),
            (guest.UserId, MembershipRole.Contributor));
        await guest.JoinAsync(project);
        var valid = TestData.Envelope(project, guest.DeviceId, 1, MsgType.Presence);
        var header = EnvelopeHeader.Parse(valid.Header);
        Envelope[] invalid =
        [
            valid with { Header = (header with { SenderDeviceId = owner.DeviceId }).ToBytes() },
            valid with { Header = (header with { MsgType = MsgType.Update }).ToBytes() },
            valid with { Header = (header with { Epoch = 1 }).ToBytes() },
            valid with { Header = (header with { ProtoVersion = 255 }).ToBytes() },
            valid with { Ciphertext = new byte[4097] },
            valid with { Nonce = [] },
            valid with { Signature = [] }
        ];
        foreach (var envelope in invalid)
            await Assert.ThrowsAsync<HubException>(() => guest.Connection.InvokeAsync("PublishPresence", envelope, Ct));
        await guest.Connection.InvokeAsync("PublishPresence", valid, Ct);
        await RemoveMemberAsync(owner.UserId, project, guest.UserId);
        await Assert.ThrowsAsync<HubException>(() => guest.Connection.InvokeAsync("PublishPresence", valid, Ct));
        Assert.Empty(await owner.CatchUpAsync(project, 0));
        await RevokeDeviceAsync(owner.UserId, owner.DeviceId);
        await owner.Closed;
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.Connection.InvokeAsync("PublishPresence",
            TestData.Envelope(project, owner.DeviceId, 1, MsgType.Presence), Ct));
        foreach (var entry in _server.Logs.Entries)
        {
            var text = entry.Message + entry.Exception + entry.ScopeText;
            foreach (var payload in invalid.Append(valid).SelectMany(value => new[] { value.Ciphertext, value.Nonce, value.Signature }).Where(value => value.Length > 0))
            {
                Assert.DoesNotContain(Convert.ToBase64String(payload), text);
                Assert.DoesNotContain(Convert.ToHexString(payload), text);
            }
            Assert.DoesNotContain(entry.Values.Concat(entry.ScopeValues), value => value is byte[] or Envelope);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldLeaseOperationsRecheckRevocationOnAnAlreadyOpenConnection(bool revokeDevice)
    {
        var writer = await ConnectAsync();
        var project = await database.CreateProjectAsync((writer.UserId, MembershipRole.Contributor));
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        await writer.Connection.InvokeAsync<FieldLeaseGrant>("AcquireFieldLease", request, Ct);
        var envelope = TestData.Envelope(project, writer.DeviceId, 1);
        await writer.Connection.InvokeAsync<long>("PushFieldEdit", request, envelope, Ct);
        await using (var db = database.CreateContext())
        {
            if (revokeDevice) await db.Devices.Where(device => device.Id == writer.DeviceId)
                .ExecuteUpdateAsync(set => set.SetProperty(device => device.RevokedAt, DateTimeOffset.UtcNow), Ct);
            else await db.Memberships.Where(member => member.ProjectId == project && member.UserId == writer.UserId).ExecuteDeleteAsync(Ct);
        }
        Assert.Equal(HubConnectionState.Connected, writer.Connection.State);
        await Assert.ThrowsAsync<HubException>(() => writer.Connection.InvokeAsync<FieldLeaseGrant>("AcquireFieldLease", request, Ct));
        await Assert.ThrowsAsync<HubException>(() => writer.Connection.InvokeAsync<FieldLeaseGrant?>("RenewFieldLease", request, Ct));
        await Assert.ThrowsAsync<HubException>(() => writer.Connection.InvokeAsync<bool>("ReleaseFieldLease", request, Ct));
        await Assert.ThrowsAsync<HubException>(() => writer.Connection.InvokeAsync<long>("PushFieldEdit", request, envelope, Ct));
        foreach (var entry in _server.Logs.Entries)
        {
            var text = entry.Message + entry.Exception + entry.ScopeText;
            Assert.DoesNotContain(Convert.ToBase64String(envelope.Ciphertext), text);
            Assert.DoesNotContain(Convert.ToHexString(envelope.Ciphertext), text);
            Assert.DoesNotContain(entry.Values.Concat(entry.ScopeValues), value => value is byte[] or Envelope);
        }
    }

    private async Task<TestClient> ConnectAsync(User? user = null)
    {
        user ??= await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id);
        var client = new TestClient(_server.CreateHubConnection(TestTokens.Create(user.Subject), deviceId), user.Id, deviceId);
        _clients.Add(client);

        await client.Connection.StartAsync(Ct);
        return client;
    }

    private async Task<string> ConnectExpectingRejectionAsync(string token, Guid? deviceId)
    {
        await using var connection = _server.CreateHubConnection(token, deviceId);
        return await ConnectExpectingRejectionAsync(connection);
    }

    // OnConnectedAsync runs after the handshake, so a rejection shows up either as a
    // failing StartAsync or as the connection being closed right after it.
    private static async Task<string> ConnectExpectingRejectionAsync(HubConnection connection)
    {
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += exception =>
        {
            closed.TrySetResult(exception);
            return Task.CompletedTask;
        };

        try
        {
            await connection.StartAsync(Ct);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        var error = await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(error);
        return error.Message;
    }

    private async Task AddMemberAsync(Guid projectId, Guid userId, MembershipRole role)
    {
        await using var db = database.CreateContext();
        db.Memberships.Add(new Membership { ProjectId = projectId, UserId = userId, Role = role });
        await db.SaveChangesAsync(Ct);
    }

    private async Task RevokeDeviceAsync(Guid userId, Guid deviceId)
    {
        await using var scope = _server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AccessRevocation>().RevokeDeviceAsync(userId, deviceId, Ct);
    }

    private async Task RemoveMemberAsync(Guid ownerId, Guid projectId, Guid memberId)
    {
        await using var scope = _server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AccessRevocation>().RemoveMemberAsync(ownerId, projectId, memberId, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        await _server.DisposeAsync();
    }
}
