using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class FieldLeaseTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed record Writer(Guid User, Guid Device);

    [Fact]
    public async Task GuardedEditsFenceExpiredGraphLeasesAndKeepConfirmedRetriesIdempotent()
    {
        var first = await WriterAsync();
        var second = await WriterAsync();
        var project = await database.CreateProjectAsync((first.User, MembershipRole.Owner), (second.User, MembershipRole.Contributor));
        var field = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var graph = field with { ResourceId = Guid.NewGuid(), LeaseId = Guid.NewGuid() };
        await AcquireAsync(first, field);
        await AcquireAsync(first, graph);
        await using var db = database.CreateContext();
        var service = Service(db);
        var edit = TestData.Envelope(project, first.Device, 1);
        Assert.Equal(1, await service.PushGuardedAsync(first.User, first.Device, field, graph, edit, Ct));
        await db.FieldLeases.Where(lease => lease.ProjectId == project && lease.ResourceId == graph.ResourceId)
            .ExecuteUpdateAsync(set => set.SetProperty(lease => lease.ExpiresAt, DateTimeOffset.UnixEpoch), Ct);
        var successor = graph with { LeaseId = Guid.NewGuid() };
        Assert.Equal(second.Device, (await AcquireAsync(second, successor)).DeviceId);
        Assert.NotNull(await service.RenewAsync(first.User, first.Device, field, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => service.PushGuardedAsync(first.User, first.Device, field, graph, TestData.Envelope(project, first.Device, 2), Ct));
        await Assert.ThrowsAsync<ConflictException>(() => service.PushGuardedAsync(first.User, first.Device, field, successor, TestData.Envelope(project, first.Device, 2), Ct));
        Assert.Equal(1, await service.PushGuardedAsync(first.User, first.Device, field, graph, edit, Ct));
        Assert.Single(await db.Updates.Where(update => update.ProjectId == project).ToListAsync(Ct));
    }

    [Fact]
    public async Task SimultaneousFocusGrantsOneWriterAndIndependentFieldsRemainAvailable()
    {
        var first = await WriterAsync();
        var second = await WriterAsync();
        var project = await database.CreateProjectAsync((first.User, MembershipRole.Owner), (second.User, MembershipRole.Contributor));
        var left = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var right = left with { LeaseId = Guid.NewGuid() };
        var attempts = await Task.WhenAll(AcquireAsync(first, left), AcquireAsync(second, right));
        Assert.Equal(attempts[0].DeviceId, attempts[1].DeviceId);
        Assert.Equal(attempts[0].LeaseId, attempts[1].LeaseId);
        Assert.True((attempts[0].LeaseId == left.LeaseId) != (attempts[1].LeaseId == right.LeaseId));
        var independent = right with { ResourceId = Guid.NewGuid(), LeaseId = Guid.NewGuid() };
        Assert.Equal(independent.LeaseId, (await AcquireAsync(second, independent)).LeaseId);
    }

    [Fact]
    public async Task BlurReleasesOnlyTheMatchingFocusAndOldRequestsCannotReleaseItsSuccessor()
    {
        var writer = await WriterAsync();
        var project = await database.CreateProjectAsync((writer.User, MembershipRole.Owner));
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var granted = await AcquireAsync(writer, request);
        Assert.Equal(granted.ExpiresAt, (await AcquireAsync(writer, request)).ExpiresAt);
        await using var db = database.CreateContext();
        var service = Service(db);
        Assert.Null(await service.RenewAsync(writer.User, writer.Device, request with { LeaseId = Guid.NewGuid() }, Ct));
        Assert.NotNull(await service.RenewAsync(writer.User, writer.Device, request, Ct));
        Assert.True(await service.ReleaseAsync(writer.User, writer.Device, request, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => AcquireAsync(writer, request));
        var next = request with { LeaseId = Guid.NewGuid() };
        Assert.Equal(next.LeaseId, (await AcquireAsync(writer, next)).LeaseId);
        Assert.False(await service.ReleaseAsync(writer.User, writer.Device, request, Ct));
        Assert.NotNull(await service.RenewAsync(writer.User, writer.Device, next, Ct));
        Assert.True(await service.ReleaseAsync(writer.User, writer.Device, next, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => AcquireAsync(writer, request));
    }

    [Fact]
    public async Task BlurBeforeAcquisitionRetiresOnlyThatDevicesFocus()
    {
        var first = await WriterAsync();
        var second = await WriterAsync();
        var project = await database.CreateProjectAsync((first.User, MembershipRole.Owner), (second.User, MembershipRole.Contributor));
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        await using var db = database.CreateContext();
        Assert.False(await Service(db).ReleaseAsync(first.User, first.Device, request, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => AcquireAsync(first, request));
        Assert.Equal(second.Device, (await AcquireAsync(second, request)).DeviceId);
        Assert.False(await Service(db).ReleaseAsync(first.User, first.Device, request, Ct));
        Assert.NotNull(await Service(db).RenewAsync(second.User, second.Device, request, Ct));
    }

    [Fact]
    public async Task ExpiredFocusCannotRenewOrCommitAndAReplacementStartsAfterTheLastAcceptedEdit()
    {
        var first = await WriterAsync();
        var second = await WriterAsync();
        var project = await database.CreateProjectAsync((first.User, MembershipRole.Owner), (second.User, MembershipRole.Contributor));
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        await AcquireAsync(first, request);
        await using var db = database.CreateContext();
        var service = Service(db);
        var edit = TestData.Envelope(project, first.Device, 1);
        Assert.Equal(1, await service.PushAsync(first.User, first.Device, request, edit, Ct));
        await db.FieldLeases.Where(lease => lease.ProjectId == project)
            .ExecuteUpdateAsync(set => set.SetProperty(lease => lease.ExpiresAt, DateTimeOffset.UnixEpoch), Ct);
        Assert.Null(await service.RenewAsync(first.User, first.Device, request, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => service.PushAsync(first.User, first.Device, request,
            TestData.Envelope(project, first.Device, 2), Ct));
        var replacement = await AcquireAsync(second, request with { LeaseId = Guid.NewGuid() });
        Assert.Equal(second.Device, replacement.DeviceId);
        Assert.Equal(1, replacement.ServerSeq);
        Assert.Equal(1, await service.PushAsync(first.User, first.Device, request, edit, Ct));
        Assert.Single(await db.Updates.Where(update => update.ProjectId == project).ToListAsync(Ct));
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("revoked")]
    [InlineData("viewer")]
    [InlineData("epoch")]
    public async Task EveryOperationRechecksAccess(string reason)
    {
        var writer = await WriterAsync();
        var owner = await WriterAsync();
        var project = await database.CreateProjectAsync((owner.User, MembershipRole.Owner), (writer.User, MembershipRole.Contributor));
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        await AcquireAsync(writer, request);
        await using var db = database.CreateContext();
        if (reason == "removed") await db.Memberships.Where(member => member.ProjectId == project && member.UserId == writer.User).ExecuteDeleteAsync(Ct);
        if (reason == "viewer") await db.Memberships.Where(member => member.ProjectId == project && member.UserId == writer.User)
            .ExecuteUpdateAsync(set => set.SetProperty(member => member.Role, MembershipRole.Viewer), Ct);
        if (reason == "revoked") await new DeviceRegistry(db, database.DeliveryGate).RevokeAsync(writer.User, writer.Device, Ct);
        if (reason == "epoch") await db.Projects.Where(value => value.Id == project)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.CurrentEpoch, 1), Ct);
        var service = Service(db);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.AcquireAsync(writer.User, writer.Device, request, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RenewAsync(writer.User, writer.Device, request, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReleaseAsync(writer.User, writer.Device, request, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.PushAsync(writer.User, writer.Device, request,
            TestData.Envelope(project, writer.Device, 1), Ct));
    }

    private async Task<Writer> WriterAsync()
    {
        var user = await database.CreateUserAsync();
        return new(user.Id, await database.CreateDeviceAsync(user.Id));
    }

    private FieldLeaseAccess Service(SyncDbContext db) => new(db, new DeviceRegistry(db, database.DeliveryGate), new UpdateLog(db));

    private async Task<FieldLeaseGrant> AcquireAsync(Writer writer, FieldLeaseRequest request)
    {
        await using var db = database.CreateContext();
        return await Service(db).AcquireAsync(writer.User, writer.Device, request, Ct);
    }
}
