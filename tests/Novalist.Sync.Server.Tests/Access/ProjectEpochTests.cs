using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class ProjectEpochTests(DatabaseFixture database)
{
    [Fact]
    public async Task BeginCanBeRetriedButRejectsTheSameIdentifierForAnotherDevice()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var otherDevice = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var request = new BeginProjectEpoch(project, Guid.NewGuid(), 0, 0);
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        await service.BeginAsync(owner.Id, device, request, ct);
        await service.BeginAsync(owner.Id, device, request, ct);
        await Assert.ThrowsAsync<ConflictException>(() => service.BeginAsync(owner.Id, otherDevice, request, ct));
        Assert.Single(await db.ProjectEpochStages.Where(stage => stage.ProjectId == project).ToListAsync(ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidRemovalOrReusedCheckpointSequenceRollsBackTheRotation(bool removeNonMember)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var member = await database.CreateUserAsync();
        var outsider = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (member.Id, MembershipRole.Contributor));
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        await service.RegisterAsync(owner.Id, device, project, 0, RandomNumberGenerator.GetBytes(182), ct);
        var original = TestData.Envelope(project, device, 1);
        await new UpdateLog(db).AppendAsync(owner.Id, original, ct);
        var request = new BeginProjectEpoch(project, Guid.NewGuid(), 0, 1);
        await service.BeginAsync(owner.Id, device, request, ct);
        var checkpoint = original with { Header = (EnvelopeHeader.Parse(original.Header) with { Epoch = 1 }).ToBytes() };
        await service.StageAsync(owner.Id, device, request.RotationId, checkpoint, ct);
        var grant = new ProjectEpochGrant(device, RandomNumberGenerator.GetBytes(182), RandomNumberGenerator.GetBytes(128));
        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            service.CommitAsync(owner.Id, device, new(request.RotationId, removeNonMember ? outsider.Id : member.Id, [grant]), ct));
        Assert.Contains(removeNonMember ? "not a member" : "sender sequence", error.Message);
        Assert.Equal(0U, (await service.StatusAsync(owner.Id, project, ct)).Epoch);
        Assert.True(await db.Memberships.AnyAsync(value => value.ProjectId == project && value.UserId == member.Id, ct));
        Assert.Empty(await db.ProjectEpochKeyGrants.Where(value => value.ProjectId == project).ToListAsync(ct));
        Assert.Single(await db.Updates.Where(value => value.ProjectId == project).ToListAsync(ct));
    }

    [Fact]
    public async Task ConcurrentRotationsPublishExactlyOneCheckpointAndGrantSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var otherGuest = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (guest.Id, MembershipRole.Contributor),
            (otherGuest.Id, MembershipRole.Contributor));
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        await service.RegisterAsync(owner.Id, device, project, 0, RandomNumberGenerator.GetBytes(182), ct);
        var rotation = Guid.NewGuid();
        await service.BeginAsync(owner.Id, device, new(project, rotation, 0, 0), ct);
        var envelope = new Envelope(new EnvelopeHeader(1, MsgType.Update, project, Guid.NewGuid(), 1, device, 1).ToBytes(),
            RandomNumberGenerator.GetBytes(24), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(64));
        await service.StageAsync(owner.Id, device, rotation, envelope, ct);
        await service.StageGrantAsync(owner.Id, device, rotation,
            new(device, RandomNumberGenerator.GetBytes(182), RandomNumberGenerator.GetBytes(128)), ct);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var pause = new SqlCommandPause("SELECT * FROM users", command =>
        {
            if (command.CommandText.Contains("SELECT * FROM users", StringComparison.Ordinal) && Interlocked.Increment(ref started) == 2)
                bothEntered.TrySetResult();
        });
        async Task<bool> Commit(Guid removed)
        {
            await using var connection = database.CreateContext(pause);
            var access = new ProjectEpochAccess(connection, new(connection, new AccessDeliveryGate()));
            try { await access.CommitAsync(owner.Id, device, new(rotation, removed, []), ct); return true; }
            catch (ConflictException) { return false; }
        }
        var commits = Task.WhenAll(Commit(guest.Id), Commit(otherGuest.Id));
        try
        {
            await await Task.WhenAny(bothEntered.Task, commits).WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.True(bothEntered.Task.IsCompleted, "Both transactions must pass preflight before acquiring database locks.");
            pause.Release.TrySetResult();
            Assert.Single(await commits, success => success);
        }
        finally { pause.Release.TrySetResult(); await commits; }
        Assert.Single(await db.Updates.Where(value => value.ProjectId == project).ToListAsync(ct));
        Assert.Single(await db.ProjectEpochKeyGrants.Where(value => value.ProjectId == project).ToListAsync(ct));
        Assert.Single(await db.Memberships.Where(value => value.ProjectId == project && value.UserId != owner.Id).ToListAsync(ct));
    }

    [Fact]
    public async Task ConcurrentReservationsKeepOnlyOneActiveRotationPerProject()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Reserve()
        {
            await using var db = database.CreateContext();
            var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
            await start.Task;
            try { await service.ReserveAsync(owner.Id, device, project, Guid.NewGuid(), ct); return true; }
            catch (ConflictException) { return false; }
        }
        var reservations = new[] { Reserve(), Reserve() };
        start.SetResult();
        Assert.Single(await Task.WhenAll(reservations), success => success);
        await using var checking = database.CreateContext();
        var retained = await checking.ProjectEpochStages.SingleAsync(stage => stage.ProjectId == project, ct);
        var access = new ProjectEpochAccess(checking, new(checking, database.DeliveryGate));
        await Assert.ThrowsAsync<ConflictException>(() => access.BeginAsync(owner.Id, device, new(project, Guid.NewGuid(), 0, 0), ct));
        await access.CancelAsync(owner.Id, device, retained.Id, ct);
        await access.ReserveAsync(owner.Id, device, project, Guid.NewGuid(), ct);
        Assert.Single(await checking.ProjectEpochStages.Where(stage => stage.ProjectId == project).ToListAsync(ct));
    }

    [Fact]
    public async Task ReservedRotationFencesWritesAndUnregisteredMembersCannotBlockRemoval()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var unregistered = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var guestDevice = await database.CreateDeviceAsync(guest.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (guest.Id, MembershipRole.Contributor),
            (unregistered.Id, MembershipRole.Contributor));
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        await service.RegisterAsync(owner.Id, device, project, 0, RandomNumberGenerator.GetBytes(182), ct);
        var before = TestData.Envelope(project, guestDevice, 1);
        await new UpdateLog(db).AppendAsync(guest.Id, before, ct);
        var rotation = Guid.NewGuid();
        var boundary = await service.ReserveAsync(owner.Id, device, project, rotation, ct);
        Assert.Equal(new ProjectEpochStatus(0, 1), boundary);
        Assert.Equal(boundary, await service.ReserveAsync(owner.Id, device, project, rotation, ct));
        var stage = new BeginProjectEpoch(project, rotation, boundary.Epoch, boundary.LastServerSeq);
        await service.BeginAsync(owner.Id, device, stage, ct);
        var envelope = new Envelope(new EnvelopeHeader(1, MsgType.Update, project, Guid.NewGuid(), 1, device, 1).ToBytes(),
            RandomNumberGenerator.GetBytes(24), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(64));
        await service.StageAsync(owner.Id, device, stage.RotationId, envelope, ct);
        var concurrent = envelope with { Header = new EnvelopeHeader(1, MsgType.Update, project, Guid.NewGuid(), 0, device, 2).ToBytes() };
        await Assert.ThrowsAsync<PushRejectedException>(() => new UpdateLog(db).AppendAsync(owner.Id, concurrent, ct));
        await Assert.ThrowsAsync<PushRejectedException>(() => new UpdateLog(db).AppendAsync(guest.Id, TestData.Envelope(project, guestDevice, 2), ct));
        Assert.Equal(1, await new UpdateLog(db).AppendAsync(guest.Id, before, ct));
        var grant = new ProjectEpochGrant(device, RandomNumberGenerator.GetBytes(182), RandomNumberGenerator.GetBytes(128));
        await service.StageGrantAsync(owner.Id, device, stage.RotationId, grant, ct);
        await service.CommitAsync(owner.Id, device, new(stage.RotationId, guest.Id, []), ct);
        Assert.False(await db.Memberships.AnyAsync(value => value.ProjectId == project && value.UserId == guest.Id, ct));
        Assert.True(await db.Memberships.AnyAsync(value => value.ProjectId == project && value.UserId == unregistered.Id, ct));
        Assert.Single(await db.ProjectEpochKeyGrants.Where(value => value.ProjectId == project).ToListAsync(ct));
        Assert.Equal(1U, (await service.StatusAsync(owner.Id, project, ct)).Epoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrExpiredReservationRestoresWritesAndCannotPublishAnOldCheckpoint(bool expire)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var rotation = Guid.NewGuid();
        await service.ReserveAsync(owner.Id, device, project, rotation, ct);
        if (expire)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE project_epoch_stages SET created_at = clock_timestamp() - interval '3 minutes', last_activity_at = clock_timestamp() - interval '3 minutes' WHERE id = {rotation}", ct);
        else await service.CancelAsync(owner.Id, device, rotation, ct);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await new UpdateLog(db).AppendAsync(owner.Id, TestData.Envelope(project, device, 1), ct));
        var checkpoint = TestData.Envelope(project, device, 2);
        checkpoint = checkpoint with { Header = (EnvelopeHeader.Parse(checkpoint.Header) with { Epoch = 1 }).ToBytes() };
        await Assert.ThrowsAsync<ConflictException>(() => service.StageAsync(owner.Id, device, rotation, checkpoint, ct));
        await new EphemeralStateCleanup(db).PruneAsync(ct);
        Assert.False(await db.ProjectEpochStages.AnyAsync(stage => stage.Id == rotation, ct));
        await service.CancelAsync(owner.Id, device, rotation, ct);
    }

    [Fact]
    public async Task RotationCommitsCheckpointAndRemovalTogetherAndRejectsStaleWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (guest.Id, MembershipRole.Contributor));
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        await service.RegisterAsync(owner.Id, device, project, 0, RandomNumberGenerator.GetBytes(182), ct);
        var first = new BeginProjectEpoch(project, Guid.NewGuid(), 0, 0);
        var second = first with { RotationId = Guid.NewGuid() };
        await service.BeginAsync(owner.Id, device, first, ct);
        await Assert.ThrowsAsync<ConflictException>(() => service.BeginAsync(owner.Id, device, second, ct));
        var envelope = new Envelope(new EnvelopeHeader(1, MsgType.Update, project, Guid.NewGuid(), 1, device, 1).ToBytes(),
            RandomNumberGenerator.GetBytes(24), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(64));
        Assert.Equal(1, await service.StageAsync(owner.Id, device, first.RotationId, envelope, ct));
        Assert.Equal(1, await service.StageAsync(owner.Id, device, first.RotationId, envelope, ct));
        var grant = new ProjectEpochGrant(device, RandomNumberGenerator.GetBytes(182), RandomNumberGenerator.GetBytes(128));
        await service.CommitAsync(owner.Id, device, new(first.RotationId, guest.Id, [grant]), ct);
        Assert.Equal(1U, (await service.StatusAsync(owner.Id, project, ct)).Epoch);
        Assert.False(await db.Memberships.AnyAsync(member => member.ProjectId == project && member.UserId == guest.Id, ct));
        Assert.Single(await db.Updates.Where(update => update.ProjectId == project).ToListAsync(ct));
        Assert.False(await db.ProjectEpochStages.AnyAsync(value => value.Id == second.RotationId, ct));
        var retired = await Assert.ThrowsAsync<ConflictException>(() => service.CommitAsync(owner.Id, device, new(second.RotationId, guest.Id, [grant]), ct));
        Assert.Equal("The project transition is unavailable.", retired.Message);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadGrantAsync(guest.Id, device, project, 0, ct));
        var old = envelope with { Header = new EnvelopeHeader(1, MsgType.Update, project, Guid.NewGuid(), 0, device, 2).ToBytes() };
        await Assert.ThrowsAsync<PushRejectedException>(() => new UpdateLog(db).AppendAsync(owner.Id, old, ct));
        Assert.NotNull(await service.ReadGrantAsync(owner.Id, device, project, 0, ct));
    }
}
