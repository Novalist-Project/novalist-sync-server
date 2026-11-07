using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Hubs;

public sealed class BlobReferenceReportTests(DatabaseFixture database)
{
    [Fact]
    public async Task WriterStreamsReferencesAndOmittedBlobsAreMarkedWithoutLoggingPayloads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Contributor));
        await using var hub = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        await hub.StartAsync(ct);
        var kept = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        var payloads = new[] { RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16) };
        await UploadAsync(hub, project, kept, new(0, payloads[0]), ct);
        await UploadAsync(hub, project, dropped, new(0, payloads[1]), ct);

        await ReportAsync(hub, project, [kept, kept, Guid.NewGuid()], ct);

        await using var db = database.CreateContext();
        var blobs = await db.Blobs.AsNoTracking().Where(value => value.ProjectId == project).ToDictionaryAsync(value => value.BlobId, ct);
        Assert.Null(blobs[kept].UnreferencedSince);
        Assert.NotNull(blobs[dropped].UnreferencedSince);
        Assert.Null(blobs[dropped].CollectableAt);
        Assert.Equal(2, blobs.Count);
        Assert.Contains(server.Logs.Entries, entry => entry.Message.Contains("reported 2 referenced blobs", StringComparison.Ordinal));
        foreach (var entry in server.Logs.Entries)
        {
            foreach (var payload in payloads)
            {
                Assert.DoesNotContain(Convert.ToBase64String(payload), entry.Message + entry.Exception + entry.ScopeText);
                Assert.DoesNotContain(Convert.ToHexString(payload), entry.Message + entry.Exception + entry.ScopeText, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain(entry.Values.Concat(entry.ScopeValues), value => value is byte[]);
        }
    }

    [Fact]
    public async Task ViewersAndNonmembersCannotReportAndRejectedStreamsLeaveTheConnectionUsable()
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
        await UploadAsync(writer, project, blob, new(0, new byte[16]), ct);

        await Assert.ThrowsAsync<HubException>(() => ReportAsync(reader, project, [], ct));
        await Assert.ThrowsAsync<HubException>(() => ReportAsync(writer, other, [], ct));
        var error = await Assert.ThrowsAsync<HubException>(() => ReportAsync(writer, project, [blob, Guid.Empty], ct));
        Assert.Contains("nonempty", error.Message);

        await using var db = database.CreateContext();
        Assert.Null(await db.Blobs.Where(value => value.ProjectId == project).Select(value => value.UnreferencedSince).SingleAsync(ct));
        await ReportAsync(writer, project, [], ct);
        Assert.NotNull(await db.Blobs.Where(value => value.ProjectId == project).Select(value => value.UnreferencedSince).SingleAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessLossRejectsLaterReportsOnAnOpenConnection(bool removeMembership)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var user = await database.CreateUserAsync();
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (user.Id, MembershipRole.Contributor));
        await using var hub = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        await hub.StartAsync(ct);
        var blob = Guid.NewGuid();
        await UploadAsync(hub, project, blob, new(0, new byte[16]), ct);
        await ReportAsync(hub, project, [blob], ct);

        await using var scope = server.Services.CreateAsyncScope();
        if (removeMembership)
            await scope.ServiceProvider.GetRequiredService<ProjectAccess>().RemoveMemberAsync(owner.Id, project, user.Id, ct);
        else
            await scope.ServiceProvider.GetRequiredService<DeviceRegistry>().RevokeAsync(user.Id, device, ct);

        // The services are called directly, so the connection stays open and the hub's access check must reject.
        await Assert.ThrowsAsync<HubException>(() => ReportAsync(hub, project, [], ct));
        await using var db = database.CreateContext();
        Assert.Null(await db.Blobs.Where(value => value.ProjectId == project).Select(value => value.UnreferencedSince).SingleAsync(ct));
    }

    private static Task UploadAsync(HubConnection hub, Guid project, Guid blob, BlobChunk chunk, CancellationToken ct) =>
        hub.InvokeAsync(nameof(ISyncHub.UploadBlobChunk), project, blob, chunk, ct);

    private static Task ReportAsync(HubConnection hub, Guid project, Guid[] referenced, CancellationToken ct) =>
        hub.InvokeAsync(nameof(ISyncHub.ReportBlobReferences), project, Stream(referenced), ct);

    private static async IAsyncEnumerable<Guid> Stream(Guid[] ids)
    {
        foreach (var id in ids) yield return id;
        await Task.CompletedTask;
    }
}
