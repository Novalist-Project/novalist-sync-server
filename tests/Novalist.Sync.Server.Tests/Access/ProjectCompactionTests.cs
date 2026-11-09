using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class ProjectCompactionTests(DatabaseFixture database)
{
    [Fact]
    public async Task AContributorCompactsAtTheCurrentEpochWithoutTouchingKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = await CreateTeamAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);
        for (ulong seq = 1; seq <= 3; seq++) await log.AppendAsync(team.Writer, Rotations.Update(team.Project, team.WriterDevice, seq, 0), ct);
        var epochs = Epochs(db);
        var compaction = Guid.NewGuid();

        var reserved = await epochs.ReserveCompactionAsync(team.Writer, team.WriterDevice, team.Project, compaction, ct);
        await epochs.StageAsync(team.Writer, team.WriterDevice, compaction, Rotations.Update(team.Project, team.WriterDevice, 4, 0), ct, compaction: true);
        var committed = await epochs.CommitCompactionAsync(team.Writer, team.WriterDevice, compaction, ct);

        Assert.Equal((0U, 3L), (reserved.Epoch, reserved.LastServerSeq));
        Assert.Equal((team.Project, 4L), committed);
        var stored = await db.Projects.AsNoTracking().SingleAsync(value => value.Id == team.Project, ct);
        Assert.Equal((0, 4L, 4L), (stored.CurrentEpoch, stored.HistoryStartsAt, stored.LastServerSeq));
        var boundary = await db.ProjectHistoryBoundaries.AsNoTracking().SingleAsync(value => value.ProjectId == team.Project, ct);
        Assert.Equal(4, boundary.FirstServerSeq);
        Assert.Equal(await StoredBytesAsync(db, team.Project, 4, ct), boundary.CheckpointBytes);
        Assert.False(await db.ProjectEpochKeyGrants.AnyAsync(value => value.ProjectId == team.Project, ct));
        Assert.False(await db.ProjectEpochStages.AnyAsync(value => value.ProjectId == team.Project, ct));
        Assert.Equal(0, (await db.ProjectDeviceAuthorizations.AsNoTracking().SingleAsync(value => value.DeviceId == team.WriterDevice, ct)).Epoch);
    }

    [Fact]
    public async Task CatchUpAfterACompactionStartsAtTheCheckpointAndRefusesEarlierPositions()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = await CreateTeamAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);
        for (ulong seq = 1; seq <= 3; seq++) await log.AppendAsync(team.Writer, Rotations.Update(team.Project, team.WriterDevice, seq, 0), ct);
        await CompactAsync(Epochs(db), team.Writer, team.WriterDevice, team.Project, 4, ct);
        await log.AppendAsync(team.Owner, Rotations.Update(team.Project, team.OwnerDevice, 1, 0), ct);

        foreach (var stale in new long[] { 0, 1, 2 })
            await Assert.ThrowsAsync<ConflictException>(() => log.EnsureHistoryAvailableAsync(team.Project, stale, ct));
        Assert.Equal(new[] { 4L, 5L }, await log.ReadAfterAsync(team.Project, 3, ct).Select(value => value.ServerSeq).ToListAsync(ct));
    }

    [Fact]
    public async Task ViewersAndNonMembersCannotCompact()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = await CreateTeamAsync();
        var outsider = await database.CreateUserAsync();
        var outsiderDevice = await database.CreateDeviceAsync(outsider.Id);
        await using var db = database.CreateContext();
        var epochs = Epochs(db);

        await Assert.ThrowsAsync<AccessDeniedException>(() => epochs.ReserveCompactionAsync(team.Viewer, team.ViewerDevice, team.Project, Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => epochs.ReserveCompactionAsync(outsider.Id, outsiderDevice, team.Project, Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => epochs.HistoryAsync(outsider.Id, team.Project, ct));
        await Assert.ThrowsAsync<FormatException>(() => epochs.ReserveCompactionAsync(team.Writer, team.WriterDevice, team.Project, Guid.Empty, ct));
        Assert.False(await db.ProjectEpochStages.AnyAsync(value => value.ProjectId == team.Project, ct));
    }

    [Fact]
    public async Task PushesPauseWhileACompactionIsReservedAndResumeAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = await CreateTeamAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);
        await log.AppendAsync(team.Owner, Rotations.Update(team.Project, team.OwnerDevice, 1, 0), ct);
        var epochs = Epochs(db);
        var compaction = Guid.NewGuid();
        await epochs.ReserveCompactionAsync(team.Writer, team.WriterDevice, team.Project, compaction, ct);

        await Assert.ThrowsAsync<PushRejectedException>(() => log.AppendAsync(team.Owner, Rotations.Update(team.Project, team.OwnerDevice, 2, 0), ct));

        await epochs.StageAsync(team.Writer, team.WriterDevice, compaction, Rotations.Update(team.Project, team.WriterDevice, 1, 0), ct, compaction: true);
        await epochs.CommitCompactionAsync(team.Writer, team.WriterDevice, compaction, ct);
        Assert.Equal(3, await log.AppendAsync(team.Owner, Rotations.Update(team.Project, team.OwnerDevice, 2, 0), ct));
    }

    [Fact]
    public async Task OnlyOneRotationOrCompactionRunsAtATime()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = await CreateTeamAsync();
        await using var db = database.CreateContext();
        var epochs = Epochs(db);
        var compaction = Guid.NewGuid();
        await epochs.ReserveCompactionAsync(team.Writer, team.WriterDevice, team.Project, compaction, ct);

        await Assert.ThrowsAsync<ConflictException>(() => epochs.ReserveCompactionAsync(team.Owner, team.OwnerDevice, team.Project, Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<ConflictException>(() => epochs.ReserveAsync(team.Owner, team.OwnerDevice, team.Project, Guid.NewGuid(), ct));
        Assert.Equal((0U, 0L), (await epochs.ReserveCompactionAsync(team.Writer, team.WriterDevice, team.Project, compaction, ct)) is var again
            ? (again.Epoch, again.LastServerSeq) : default);

        await epochs.CancelAsync(team.Writer, team.WriterDevice, compaction, ct, compaction: true);
        var rotation = Guid.NewGuid();
        await epochs.ReserveAsync(team.Owner, team.OwnerDevice, team.Project, rotation, ct);
        await Assert.ThrowsAsync<ConflictException>(() => epochs.ReserveCompactionAsync(team.Writer, team.WriterDevice, team.Project, Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<ConflictException>(() => epochs.ReserveCompactionAsync(team.Owner, team.OwnerDevice, team.Project, rotation, ct));
    }

    [Fact]
    public async Task ACompactionCannotBeUsedAsARotationOrTheOtherWayRound()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = await CreateTeamAsync();
        await using var db = database.CreateContext();
        var epochs = Epochs(db);
        var compaction = Guid.NewGuid();
        await epochs.ReserveCompactionAsync(team.Owner, team.OwnerDevice, team.Project, compaction, ct);

        await Assert.ThrowsAsync<ConflictException>(() =>
            epochs.StageAsync(team.Owner, team.OwnerDevice, compaction, Rotations.Update(team.Project, team.OwnerDevice, 1, 1), ct, compaction: true));
        await Assert.ThrowsAsync<ConflictException>(() =>
            epochs.StageAsync(team.Owner, team.OwnerDevice, compaction, Rotations.Update(team.Project, team.OwnerDevice, 1, 0), ct));
        await epochs.StageAsync(team.Owner, team.OwnerDevice, compaction, Rotations.Update(team.Project, team.OwnerDevice, 1, 0), ct, compaction: true);
        await Assert.ThrowsAsync<ConflictException>(() =>
            epochs.StageGrantAsync(team.Owner, team.OwnerDevice, compaction, new(team.OwnerDevice, new byte[182], new byte[128]), ct));
        await Assert.ThrowsAsync<ConflictException>(() =>
            epochs.CommitAsync(team.Owner, team.OwnerDevice, new(compaction, null, [new(team.OwnerDevice, new byte[182], new byte[128])]), ct));
        await Assert.ThrowsAsync<ConflictException>(() => epochs.CancelAsync(team.Owner, team.OwnerDevice, compaction, ct));

        await epochs.CancelAsync(team.Owner, team.OwnerDevice, compaction, ct, compaction: true);
        var rotation = Guid.NewGuid();
        await epochs.ReserveAsync(team.Owner, team.OwnerDevice, team.Project, rotation, ct);
        await Assert.ThrowsAsync<ConflictException>(() =>
            epochs.StageAsync(team.Owner, team.OwnerDevice, rotation, Rotations.Update(team.Project, team.OwnerDevice, 2, 1), ct, compaction: true));
        await Assert.ThrowsAsync<ConflictException>(() => epochs.CommitCompactionAsync(team.Owner, team.OwnerDevice, rotation, ct));
        await Assert.ThrowsAsync<ConflictException>(() => epochs.CancelAsync(team.Owner, team.OwnerDevice, rotation, ct, compaction: true));
    }

    [Fact]
    public async Task OnlyTheReservingDeviceCanContinueOrCancelACompaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = await CreateTeamAsync();
        await using var db = database.CreateContext();
        var epochs = Epochs(db);
        var compaction = Guid.NewGuid();
        await epochs.ReserveCompactionAsync(team.Writer, team.WriterDevice, team.Project, compaction, ct);
        await epochs.StageAsync(team.Writer, team.WriterDevice, compaction, Rotations.Update(team.Project, team.WriterDevice, 1, 0), ct, compaction: true);

        await Assert.ThrowsAsync<ConflictException>(() => epochs.CancelAsync(team.Owner, team.OwnerDevice, compaction, ct, compaction: true));
        await Assert.ThrowsAsync<ConflictException>(() => epochs.CommitCompactionAsync(team.Owner, team.OwnerDevice, compaction, ct));

        await epochs.CancelAsync(team.Writer, team.WriterDevice, compaction, ct, compaction: true);
        await epochs.CancelAsync(team.Writer, team.WriterDevice, compaction, ct, compaction: true);
        Assert.False(await db.ProjectEpochStages.AnyAsync(value => value.ProjectId == team.Project, ct));
        Assert.Equal(1, (await db.Projects.AsNoTracking().SingleAsync(value => value.Id == team.Project, ct)).HistoryStartsAt);
        await Assert.ThrowsAsync<ConflictException>(() => epochs.CommitCompactionAsync(team.Writer, team.WriterDevice, compaction, ct));
    }

    [Fact]
    public async Task CommitRefusesAMissingCheckpointOrAMovedPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = await CreateTeamAsync();
        await using var db = database.CreateContext();
        var epochs = Epochs(db);
        var compaction = Guid.NewGuid();
        await epochs.ReserveCompactionAsync(team.Writer, team.WriterDevice, team.Project, compaction, ct);

        await Assert.ThrowsAsync<ConflictException>(() => epochs.CommitCompactionAsync(team.Writer, team.WriterDevice, compaction, ct));

        await epochs.StageAsync(team.Writer, team.WriterDevice, compaction, Rotations.Update(team.Project, team.WriterDevice, 1, 0), ct, compaction: true);
        await db.Projects.Where(value => value.Id == team.Project).ExecuteUpdateAsync(set => set.SetProperty(value => value.LastServerSeq, 5), ct);
        await Assert.ThrowsAsync<ConflictException>(() => epochs.CommitCompactionAsync(team.Writer, team.WriterDevice, compaction, ct));

        var stored = await db.Projects.AsNoTracking().SingleAsync(value => value.Id == team.Project, ct);
        Assert.Equal(1, stored.HistoryStartsAt);
        Assert.False(await db.ProjectHistoryBoundaries.AnyAsync(value => value.ProjectId == team.Project, ct));
        Assert.False(await db.Updates.AnyAsync(value => value.ProjectId == team.Project, ct));
    }

    [Fact]
    public async Task HistoryReportsTheStoredBytesSinceTheBoundaryAndTheNewestCheckpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var team = await CreateTeamAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);
        var epochs = Epochs(db);
        for (ulong seq = 1; seq <= 3; seq++) await log.AppendAsync(team.Writer, Rotations.Update(team.Project, team.WriterDevice, seq, 0), ct);

        var before = await epochs.HistoryAsync(team.Viewer, team.Project, ct);
        Assert.Equal((1L, 3L, await StoredBytesAsync(db, team.Project, 1, ct)), (before.HistoryStartsAt, before.LastServerSeq, before.BytesSinceBoundary));
        Assert.Null(before.CheckpointBytes);
        Assert.Null(before.LastCompactedAt);

        await CompactAsync(epochs, team.Writer, team.WriterDevice, team.Project, 4, ct);
        await log.AppendAsync(team.Writer, Rotations.Update(team.Project, team.WriterDevice, 5, 0), ct);
        var after = await epochs.HistoryAsync(team.Writer, team.Project, ct);
        Assert.Equal((4L, 5L, await StoredBytesAsync(db, team.Project, 4, ct)), (after.HistoryStartsAt, after.LastServerSeq, after.BytesSinceBoundary));
        Assert.Equal(await StoredBytesAsync(db, team.Project, 4, ct) - await StoredBytesAsync(db, team.Project, 5, ct), after.CheckpointBytes);
        Assert.NotNull(after.LastCompactedAt);

        // The rotation helper grants one device only.
        await db.ProjectDeviceAuthorizations.Where(value => value.DeviceId != team.OwnerDevice && value.ProjectId == team.Project).ExecuteDeleteAsync(ct);
        await Rotations.RotateAsync(epochs, team.Owner, team.OwnerDevice, team.Project, 1, ct);
        var rotated = await epochs.HistoryAsync(team.Owner, team.Project, ct);
        Assert.Equal(6, rotated.HistoryStartsAt);
        Assert.Equal(await StoredBytesAsync(db, team.Project, 6, ct), rotated.CheckpointBytes);
    }

    private ProjectEpochAccess Epochs(SyncDbContext db) => new(db, new(db, database.DeliveryGate));

    private static async Task CompactAsync(ProjectEpochAccess epochs, Guid user, Guid device, Guid project, ulong checkpointSenderSeq, CancellationToken ct)
    {
        var compaction = Guid.NewGuid();
        var reserved = await epochs.ReserveCompactionAsync(user, device, project, compaction, ct);
        await epochs.StageAsync(user, device, compaction, Rotations.Update(project, device, checkpointSenderSeq, reserved.Epoch), ct, compaction: true);
        await epochs.CommitCompactionAsync(user, device, compaction, ct);
    }

    private static Task<long> StoredBytesAsync(SyncDbContext db, Guid project, long from, CancellationToken ct) =>
        db.Updates.Where(value => value.ProjectId == project && value.ServerSeq >= from)
            .SumAsync(value => (long)value.Header.Length + value.Nonce.Length + value.Ciphertext.Length + value.Signature.Length, ct);

    private sealed record Team(Guid Project, Guid Owner, Guid OwnerDevice, Guid Writer, Guid WriterDevice, Guid Viewer, Guid ViewerDevice);

    private async Task<Team> CreateTeamAsync()
    {
        var owner = await database.CreateUserAsync();
        var writer = await database.CreateUserAsync();
        var viewer = await database.CreateUserAsync();
        var ownerDevice = await database.CreateDeviceAsync(owner.Id);
        var writerDevice = await database.CreateDeviceAsync(writer.Id);
        var viewerDevice = await database.CreateDeviceAsync(viewer.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (writer.Id, MembershipRole.Contributor),
            (viewer.Id, MembershipRole.Viewer));
        await using (var db = database.CreateContext())
        {
            foreach (var device in new[] { ownerDevice, writerDevice, viewerDevice })
                db.ProjectDeviceAuthorizations.Add(new() { ProjectId = project, DeviceId = device, Epoch = 0, Certificate = new byte[182] });
            await db.SaveChangesAsync();
        }
        return new(project, owner.Id, ownerDevice, writer.Id, writerDevice, viewer.Id, viewerDevice);
    }
}
