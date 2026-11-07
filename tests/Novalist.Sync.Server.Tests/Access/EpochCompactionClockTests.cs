using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

// Own database: cleanup with a shifted clock deletes across all projects, which would hit other tests.
public sealed class EpochCompactionClockTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly DatabaseFixture database = new();
    public ValueTask InitializeAsync() => database.InitializeAsync();
    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task StagingKeepsTheReservationAliveAndPushesBlockedUntilItGoesIdle()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        await using var db = database.CreateContext(clock);
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var rotation = Guid.NewGuid();
        await epochs.ReserveAsync(owner, device, project, rotation, ct);

        for (ulong seq = 1; seq <= 4; seq++)
        {
            clock.Now += TimeSpan.FromSeconds(90);
            await epochs.StageAsync(owner, device, rotation, Rotations.Update(project, device, seq, 1), ct);
        }
        Assert.True(clock.Now - Start > ProjectEpochAccess.ReservationLifetime);
        await Assert.ThrowsAsync<PushRejectedException>(() => new UpdateLog(db).AppendAsync(owner, Rotations.Update(project, device, 10, 0), ct));

        clock.Now += ProjectEpochAccess.ReservationLifetime;
        await Assert.ThrowsAsync<ConflictException>(() => epochs.StageAsync(owner, device, rotation, Rotations.Update(project, device, 5, 1), ct));
        Assert.Equal(1, await new UpdateLog(db).AppendAsync(owner, Rotations.Update(project, device, 10, 0), ct));
    }

    [Fact]
    public async Task AnActiveReservationStillEndsAtTheMaximumLifetime()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        await using var db = database.CreateContext(clock);
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var rotation = Guid.NewGuid();
        await epochs.ReserveAsync(owner, device, project, rotation, ct);
        var step = TimeSpan.FromMinutes(1);
        ulong seq = 0;

        while (clock.Now + step < Start + ProjectEpochAccess.ReservationMaxLifetime)
        {
            clock.Now += step;
            await epochs.StageAsync(owner, device, rotation, Rotations.Update(project, device, ++seq, 1), ct);
        }
        clock.Now = Start + ProjectEpochAccess.ReservationMaxLifetime;

        await Assert.ThrowsAsync<ConflictException>(() => epochs.StageAsync(owner, device, rotation, Rotations.Update(project, device, ++seq, 1), ct));
        await new EphemeralStateCleanup(db).PruneAsync(ct);
        Assert.False(await db.ProjectEpochStages.AnyAsync(stage => stage.Id == rotation, ct));
    }

    [Fact]
    public async Task SupersededUpdatesAreDeletedOnlyAfterTheGracePeriod()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        await using var db = database.CreateContext(clock);
        await Rotations.AuthorizeAsync(db, project, device, ct);
        var log = new UpdateLog(db);
        for (ulong seq = 1; seq <= 3; seq++) await log.AppendAsync(owner, Rotations.Update(project, device, seq, 0), ct);
        await Rotations.RotateAsync(new ProjectEpochAccess(db, new(db, database.DeliveryGate)), owner, device, project, 4, ct);
        var cleanup = new EphemeralStateCleanup(db, Options.Create(new UpdateHistoryOptions { CompactionGracePeriod = TimeSpan.FromDays(30) }));

        clock.Now = Start + TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1);
        Assert.Equal(0, await cleanup.PruneAsync(ct));
        Assert.Equal(new[] { 1L, 2L, 3L, 4L }, await SequencesAsync(db, project, ct));

        clock.Now = Start + TimeSpan.FromDays(30);
        Assert.Equal(3, await cleanup.PruneAsync(ct));
        Assert.Equal(new[] { 4L }, await SequencesAsync(db, project, ct));
        Assert.Equal(new[] { 4L }, await log.ReadAfterAsync(project, 3, ct).Select(value => value.ServerSeq).ToListAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalStagingRetriesPersistActivityWithoutExtendingTheMaximumLifetime(bool grantRetry)
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        var rotation = Guid.NewGuid();
        var checkpoint = Rotations.Update(project, device, 1, 1);
        var grant = new ProjectEpochGrant(device, new byte[182], new byte[128]);
        await using (var db = database.CreateContext(clock))
        {
            var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
            await epochs.ReserveAsync(owner, device, project, rotation, ct);
            await epochs.StageAsync(owner, device, rotation, checkpoint, ct);
            await epochs.StageGrantAsync(owner, device, rotation, grant, ct);
        }

        while (clock.Now + TimeSpan.FromSeconds(90) < Start + ProjectEpochAccess.ReservationMaxLifetime)
        {
            clock.Now += TimeSpan.FromSeconds(90);
            await using (var db = database.CreateContext(clock))
            {
                var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
                if (grantRetry) await epochs.StageGrantAsync(owner, device, rotation, grant, ct);
                else Assert.Equal(1, await epochs.StageAsync(owner, device, rotation, checkpoint, ct));
            }
            await using var persisted = database.CreateContext();
            var stage = await persisted.ProjectEpochStages.SingleAsync(value => value.Id == rotation, ct);
            Assert.Equal(clock.Now, stage.LastActivityAt);
            Assert.Equal(Start, stage.CreatedAt);
            Assert.Equal(1, stage.Count);
        }

        clock.Now = Start + ProjectEpochAccess.ReservationMaxLifetime;
        await using var expired = database.CreateContext(clock);
        var service = new ProjectEpochAccess(expired, new(expired, database.DeliveryGate));
        if (grantRetry)
            await Assert.ThrowsAsync<ConflictException>(() => service.StageGrantAsync(owner, device, rotation, grant, ct));
        else
            await Assert.ThrowsAsync<ConflictException>(() => service.StageAsync(owner, device, rotation, checkpoint, ct));
    }

    [Fact]
    public async Task FrequentRotationsDoNotPostponeDeletingOlderEpochs()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        await using var db = database.CreateContext(clock);
        await Rotations.AuthorizeAsync(db, project, device, ct);
        var log = new UpdateLog(db);
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        for (ulong seq = 1; seq <= 3; seq++) await log.AppendAsync(owner, Rotations.Update(project, device, seq, 0), ct);
        await Rotations.RotateAsync(epochs, owner, device, project, 4, ct);
        clock.Now = Start + TimeSpan.FromDays(20);
        for (ulong seq = 5; seq <= 6; seq++) await log.AppendAsync(owner, Rotations.Update(project, device, seq, 1), ct);
        await Rotations.RotateAsync(epochs, owner, device, project, 7, ct);
        var cleanup = new EphemeralStateCleanup(db, Options.Create(new UpdateHistoryOptions { CompactionGracePeriod = TimeSpan.FromDays(30) }));

        clock.Now = Start + TimeSpan.FromDays(31);
        Assert.Equal(3, await cleanup.PruneAsync(ct));
        Assert.Equal(new[] { 4L, 5L, 6L, 7L }, await SequencesAsync(db, project, ct));

        clock.Now = Start + TimeSpan.FromDays(51);
        Assert.Equal(3, await cleanup.PruneAsync(ct));
        Assert.Equal(new[] { 7L }, await SequencesAsync(db, project, ct));
    }

    [Fact]
    public async Task CompactionDeletesInBoundedBatchesPerRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, device, project) = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        await using var db = database.CreateContext(clock);
        await Rotations.AuthorizeAsync(db, project, device, ct);
        var log = new UpdateLog(db);
        for (ulong seq = 1; seq <= 5; seq++) await log.AppendAsync(owner, Rotations.Update(project, device, seq, 0), ct);
        await Rotations.RotateAsync(new ProjectEpochAccess(db, new(db, database.DeliveryGate)), owner, device, project, 6, ct);
        var cleanup = new EphemeralStateCleanup(db, Options.Create(new UpdateHistoryOptions
        {
            CompactionGracePeriod = TimeSpan.Zero,
            CompactionBatchSize = 2,
            CompactionMaxBatchesPerRun = 2
        }));

        Assert.Equal(4, await cleanup.PruneAsync(ct));
        Assert.Equal(1, await cleanup.PruneAsync(ct));
        Assert.Equal(0, await cleanup.PruneAsync(ct));
        Assert.Equal(new[] { 6L }, await SequencesAsync(db, project, ct));
    }

    private static Task<List<long>> SequencesAsync(SyncDbContext db, Guid project, CancellationToken ct) =>
        db.Updates.Where(value => value.ProjectId == project).OrderBy(value => value.ServerSeq).Select(value => value.ServerSeq).ToListAsync(ct);

    private async Task<(Guid Owner, Guid Device, Guid Project)> CreateProjectAsync()
    {
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        return (owner.Id, device, project);
    }
}
