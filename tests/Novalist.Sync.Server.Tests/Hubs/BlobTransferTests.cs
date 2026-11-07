using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using NSec.Cryptography;

namespace Novalist.Sync.Server.Tests.Hubs;

public sealed class BlobTransferTests(DatabaseFixture database)
{
    [Fact]
    public async Task ChunksRoundTripInOrderResumeAndRejectChangedRetriesWithoutLoggingPayloads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await using var hub = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        await hub.StartAsync(ct);
        var blob = Guid.NewGuid();
        var first = new BlobChunk(0, RandomNumberGenerator.GetBytes(BlobTransfer.MaximumCiphertextSize));
        var last = new BlobChunk(1, RandomNumberGenerator.GetBytes(16));
        await UploadAsync(hub, project, blob, last, ct);
        Assert.Null(await DownloadChunkAsync(hub, project, blob, 0, ct));
        Assert.Equal(last.Ciphertext, (await DownloadChunkAsync(hub, project, blob, 1, ct))!.Ciphertext);
        await UploadAsync(hub, project, blob, first, ct);
        await UploadAsync(hub, project, blob, first, ct);
        var chunks = await DownloadAsync(hub, project, blob, -1, ct);
        Assert.Equal(new[] { 0, 1 }, chunks.Select(chunk => chunk.Index));
        Assert.Equal(first.Ciphertext, chunks[0].Ciphertext);
        Assert.Equal(last.Ciphertext, chunks[1].Ciphertext);
        Assert.Equal(last.Ciphertext, Assert.Single(await DownloadAsync(hub, project, blob, 0, ct)).Ciphertext);
        Assert.Empty(await DownloadAsync(hub, project, blob, 1, ct));
        Assert.Empty(await DownloadAsync(hub, project, Guid.NewGuid(), -1, ct));
        var error = await Assert.ThrowsAsync<HubException>(() => UploadAsync(hub, project, blob,
            first with { Ciphertext = RandomNumberGenerator.GetBytes(first.Ciphertext.Length) }, ct));
        Assert.Contains("cannot change", error.Message);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.BlobChunks.CountAsync(value => value.ProjectId == project, ct));
        Assert.Equal(0, (await db.Projects.SingleAsync(value => value.Id == project, ct)).LastServerSeq);
        foreach (var entry in server.Logs.Entries)
        {
            Assert.DoesNotContain(Convert.ToBase64String(first.Ciphertext), entry.Message + entry.Exception + entry.ScopeText);
            Assert.DoesNotContain(Convert.ToHexString(first.Ciphertext), entry.Message + entry.Exception + entry.ScopeText);
            Assert.DoesNotContain(entry.Values.Concat(entry.ScopeValues), value => value is byte[]);
        }
    }

    [Fact]
    public async Task ViewersCanOnlyReadAndProjectBoundariesPreventBlobDisclosure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var owner = await database.CreateUserAsync();
        var viewer = await database.CreateUserAsync();
        var ownerDevice = await database.CreateDeviceAsync(owner.Id);
        var viewerDevice = await database.CreateDeviceAsync(viewer.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (viewer.Id, MembershipRole.Viewer));
        var other = await database.CreateProjectAsync((viewer.Id, MembershipRole.Owner));
        await using var writer = server.CreateHubConnection(TestTokens.Create(owner.Subject), ownerDevice);
        await using var reader = server.CreateHubConnection(TestTokens.Create(viewer.Subject), viewerDevice);
        await writer.StartAsync(ct);
        await reader.StartAsync(ct);
        var blob = Guid.NewGuid();
        var chunk = new BlobChunk(0, RandomNumberGenerator.GetBytes(32));
        await UploadAsync(writer, project, blob, chunk, ct);
        Assert.Equal(chunk.Ciphertext, Assert.Single(await DownloadAsync(reader, project, blob, -1, ct)).Ciphertext);
        await Assert.ThrowsAsync<HubException>(() => UploadAsync(reader, project, blob, chunk, ct));
        await Assert.ThrowsAsync<HubException>(() => UploadAsync(writer, other, blob, chunk, ct));
        await Assert.ThrowsAsync<HubException>(() => DownloadAsync(writer, other, blob, -1, ct));
        Assert.Empty(await DownloadAsync(reader, other, blob, -1, ct));
        await using var db = database.CreateContext();
        await db.Memberships.Where(value => value.ProjectId == project && value.UserId == viewer.Id).ExecuteDeleteAsync(ct);
        await Assert.ThrowsAsync<HubException>(() => DownloadAsync(reader, project, blob, -1, ct));
        await Assert.ThrowsAsync<HubException>(() => UploadAsync(reader, project, blob, chunk, ct));
    }

    [Theory]
    [InlineData(-1, 16)]
    [InlineData(0, 0)]
    [InlineData(0, 15)]
    [InlineData(0, 65553)]
    public async Task InvalidChunksAndCursorsAreRejected(int index, int size)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await using var hub = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        await hub.StartAsync(ct);
        var blob = Guid.NewGuid();
        await Assert.ThrowsAsync<HubException>(() => UploadAsync(hub, project, blob, new(index, new byte[size]), ct));
        await Assert.ThrowsAsync<HubException>(() => UploadAsync(hub, project, Guid.Empty, new(0, new byte[16]), ct));
        await Assert.ThrowsAsync<HubException>(() => DownloadAsync(hub, project, blob, -2, ct));
        await using var db = database.CreateContext();
        Assert.False(await db.BlobChunks.AnyAsync(value => value.ProjectId == project, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedDeviceRequiresPossessionAndAccessLossStopsLaterChunkRequests(bool removeMembership)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        using var signing = Key.Create(SignatureAlgorithm.Ed25519);
        using var receiving = Key.Create(KeyAgreementAlgorithm.X25519);
        await using (var db = database.CreateContext())
        {
            var stored = await db.Devices.SingleAsync(value => value.Id == device, ct);
            stored.SigningPublicKey = signing.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            stored.ReceivingPublicKey = receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            await db.SaveChangesAsync(ct);
        }
        await using var connection = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        await connection.StartAsync(ct);
        var blob = Guid.NewGuid();
        var chunk = new BlobChunk(0, RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<HubException>(() => UploadAsync(connection, project, blob, chunk, ct));
        await Assert.ThrowsAsync<HubException>(() => DownloadAsync(connection, project, blob, -1, ct));
        var challenge = await connection.InvokeAsync<DeviceConnectionChallenge>(nameof(ISyncHub.GetDeviceChallenge), ct);
        await connection.InvokeAsync(nameof(ISyncHub.AuthenticateDevice),
            SignatureAlgorithm.Ed25519.Sign(signing, DeviceProof.EncodeConnection(challenge)), ct);
        await UploadAsync(connection, project, blob, chunk, ct);
        await UploadAsync(connection, project, blob, chunk with { Index = 1 }, ct);

        await using var scope = server.Services.CreateAsyncScope();
        Assert.Equal(chunk.Ciphertext, (await DownloadChunkAsync(connection, project, blob, 0, ct))!.Ciphertext);
        if (removeMembership)
        {
            var owner = await database.CreateUserAsync();
            await using (var db = database.CreateContext())
            {
                db.Memberships.Add(new() { ProjectId = project, UserId = owner.Id, Role = MembershipRole.Owner });
                await db.SaveChangesAsync(ct);
            }
            await scope.ServiceProvider.GetRequiredService<ProjectAccess>().RemoveMemberAsync(owner.Id, project, user.Id, ct);
        }
        else
            await scope.ServiceProvider.GetRequiredService<DeviceRegistry>().RevokeAsync(user.Id, device, ct);
        await Assert.ThrowsAsync<HubException>(() => DownloadAsync(connection, project, blob, 0, ct));
        await Assert.ThrowsAsync<HubException>(() => UploadAsync(connection, project, blob, chunk with { Index = 2 }, ct));
    }

    [Fact]
    public async Task ConcurrentUploadsCannotOverwriteAChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await using var first = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        await using var second = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        await first.StartAsync(ct);
        await second.StartAsync(ct);
        var blob = Guid.NewGuid();
        var left = new BlobChunk(0, RandomNumberGenerator.GetBytes(32));
        var right = new BlobChunk(0, RandomNumberGenerator.GetBytes(32));
        var errors = await Task.WhenAll(
            Record.ExceptionAsync(() => UploadAsync(first, project, blob, left, ct)).AsTask(),
            Record.ExceptionAsync(() => UploadAsync(second, project, blob, right, ct)).AsTask());
        Assert.Single(errors, error => error is null);
        Assert.IsType<HubException>(Assert.Single(errors, error => error is not null));
        var stored = await DownloadChunkAsync(first, project, blob, 0, ct);
        Assert.NotNull(stored);
        Assert.Equal(errors[0] is null ? left.Ciphertext : right.Ciphertext, stored.Ciphertext);
    }

    [Fact]
    public async Task DeletingProjectRemovesItsBlobChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await using var db = database.CreateContext();
        db.BlobChunks.Add(new() { ProjectId = project, BlobId = Guid.NewGuid(), Index = 0, Ciphertext = new byte[16] });
        await db.SaveChangesAsync(ct);
        await new ProjectAccess(db, database.DeliveryGate).DeleteAsync(user.Id, project, ct);
        Assert.False(await db.BlobChunks.AnyAsync(value => value.ProjectId == project, ct));
    }

    private static Task UploadAsync(HubConnection hub, Guid project, Guid blob, BlobChunk chunk, CancellationToken ct) =>
        hub.InvokeAsync(nameof(ISyncHub.UploadBlobChunk), project, blob, chunk, ct);

    private static Task<BlobChunk?> DownloadChunkAsync(HubConnection hub, Guid project, Guid blob, int index, CancellationToken ct) =>
        hub.InvokeAsync<BlobChunk?>(nameof(ISyncHub.DownloadBlobChunk), project, blob, index, ct);

    private static async Task<List<BlobChunk>> DownloadAsync(HubConnection hub, Guid project, Guid blob, int cursor, CancellationToken ct)
    {
        var chunks = new List<BlobChunk>();
        while (await DownloadChunkAsync(hub, project, blob, ++cursor, ct) is { } chunk) chunks.Add(chunk);
        return chunks;
    }
}
