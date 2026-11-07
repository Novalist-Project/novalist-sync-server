using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class EpochCompactionTests(DatabaseFixture database)
{
    [Fact]
    public async Task CommitMovesTheHistoryBoundaryToTheSignedGrantBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        await using var db = database.CreateContext();
        await Rotations.AuthorizeAsync(db, project, device, ct);
        var log = new UpdateLog(db);
        for (ulong seq = 1; seq <= 3; seq++) await log.AppendAsync(owner, Rotations.Update(project, device, seq, 0), ct);
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));

        await Rotations.RotateAsync(epochs, owner, device, project, 4, ct);

        var stored = await db.Projects.AsNoTracking().SingleAsync(value => value.Id == project, ct);
        Assert.Equal(1, stored.CurrentEpoch);
        Assert.Equal(4, stored.HistoryStartsAt);
        Assert.Equal(4, (await epochs.ReadGrantAsync(owner, device, project, 0, ct))!.FirstServerSeq);
        Assert.Equal(new[] { 4L }, await db.ProjectHistoryBoundaries.Where(value => value.ProjectId == project).Select(value => value.FirstServerSeq).ToListAsync(ct));
        Assert.Equal(4, await db.Updates.CountAsync(value => value.ProjectId == project, ct));
    }

    [Fact]
    public async Task FailedCommitLeavesTheHistoryBoundaryUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        await using var db = database.CreateContext();
        await Rotations.AuthorizeAsync(db, project, device, ct);
        await new UpdateLog(db).AppendAsync(owner, Rotations.Update(project, device, 1, 0), ct);
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var rotation = Guid.NewGuid();
        await epochs.ReserveAsync(owner, device, project, rotation, ct);
        await epochs.StageAsync(owner, device, rotation, Rotations.Update(project, device, 2, 1), ct);
        // Simulates the log moving past the reserved boundary, which CheckBoundary and the CAS must catch.
        await db.Projects.Where(value => value.Id == project).ExecuteUpdateAsync(set => set.SetProperty(value => value.LastServerSeq, 5), ct);

        await Assert.ThrowsAsync<ConflictException>(() =>
            epochs.CommitAsync(owner, device, new(rotation, null, [new(device, new byte[182], new byte[128])]), ct));

        var stored = await db.Projects.AsNoTracking().SingleAsync(value => value.Id == project, ct);
        Assert.Equal(0, stored.CurrentEpoch);
        Assert.Equal(1, stored.HistoryStartsAt);
        Assert.False(await db.ProjectHistoryBoundaries.AnyAsync(value => value.ProjectId == project, ct));
    }

    [Fact]
    public async Task CatchUpAfterRotationStartsAtTheCheckpointAndRefusesEarlierPositions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        await using var db = database.CreateContext();
        await Rotations.AuthorizeAsync(db, project, device, ct);
        var log = new UpdateLog(db);
        for (ulong seq = 1; seq <= 3; seq++) await log.AppendAsync(owner, Rotations.Update(project, device, seq, 0), ct);
        await Rotations.RotateAsync(new ProjectEpochAccess(db, new(db, database.DeliveryGate)), owner, device, project, 4, ct);
        await log.AppendAsync(owner, Rotations.Update(project, device, 5, 1), ct);

        foreach (var stale in new long[] { 0, 1, 2 })
            await Assert.ThrowsAsync<ConflictException>(() => log.EnsureHistoryAvailableAsync(project, stale, ct));
        await log.EnsureHistoryAvailableAsync(project, 3, ct);
        await log.EnsureHistoryAvailableAsync(project, 5, ct);
        var caughtUp = await log.ReadAfterAsync(project, 3, ct).Select(value => value.ServerSeq).ToListAsync(ct);
        Assert.Equal(new[] { 4L, 5L }, caughtUp);
    }

    [Fact]
    public async Task CatchUpOfAProjectThatNeverRotatedStartsAtTheBeginning()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        await using var db = database.CreateContext();
        await new UpdateLog(db).AppendAsync(owner, Rotations.Update(project, device, 1, 0), ct);

        await new UpdateLog(db).EnsureHistoryAvailableAsync(project, 0, ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => new UpdateLog(db).EnsureHistoryAvailableAsync(Guid.NewGuid(), 0, ct));
    }

    [Fact]
    public async Task CommitLogContainsSizesButNoPayloadBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        await using var db = database.CreateContext();
        await Rotations.AuthorizeAsync(db, project, device, ct);
        var captured = new CapturedLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(captured));
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate), factory.CreateLogger<ProjectEpochAccess>());
        var rotation = Guid.NewGuid();
        await epochs.ReserveAsync(owner, device, project, rotation, ct);
        var checkpoint = Rotations.Update(project, device, 1, 1);
        await epochs.StageAsync(owner, device, rotation, checkpoint, ct);

        await epochs.CommitAsync(owner, device, new(rotation, null, [new(device, new byte[182], new byte[128])]), ct);

        var entry = Assert.Single(captured.Entries, value => value.Message.Contains("Committed epoch", StringComparison.Ordinal));
        Assert.Contains(project.ToString(), entry.Message, StringComparison.Ordinal);
        var secrets = new[] { checkpoint.Ciphertext, checkpoint.Nonce, checkpoint.Signature }
            .SelectMany(bytes => new[] { Convert.ToBase64String(bytes), Convert.ToHexString(bytes), Convert.ToHexStringLower(bytes) });
        foreach (var logged in captured.Entries.SelectMany(value => new[] { value.Message, value.ScopeText }
            .Concat(value.Values.Select(item => item?.ToString())).Concat(value.ScopeValues.Select(item => item?.ToString()))))
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, logged ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingAProjectRemovesItsHistoryBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        await using var db = database.CreateContext();
        await Rotations.AuthorizeAsync(db, project, device, ct);
        await Rotations.RotateAsync(new ProjectEpochAccess(db, new(db, database.DeliveryGate)), owner, device, project, 1, ct);

        await new ProjectAccess(db, database.DeliveryGate).DeleteAsync(owner, project, ct);

        Assert.False(await db.ProjectHistoryBoundaries.AnyAsync(value => value.ProjectId == project, ct));
    }

    private async Task<(Guid Owner, Guid Device, Guid Project)> CreateProjectAsync()
    {
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        return (owner.Id, device, project);
    }
}
