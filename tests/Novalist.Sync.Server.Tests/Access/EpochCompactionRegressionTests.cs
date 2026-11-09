using System.Data.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class EpochCompactionRegressionTests : IAsyncLifetime
{
    private readonly DatabaseFixture database = new();
    public ValueTask InitializeAsync() => database.InitializeAsync();
    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task RotationAndPruningBetweenCatchUpPagesRejectsInsteadOfSkippingUpdates()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        await using var writer = database.CreateContext();
        await Rotations.AuthorizeAsync(writer, project, device, ct);
        var log = new UpdateLog(writer);
        for (ulong seq = 1; seq <= 130; seq++)
            await log.AppendAsync(owner.Id, Rotations.Update(project, device, seq, 0), ct);
        await using var reader = database.CreateContext();
        await using var stream = new UpdateLog(reader).ReadAfterAsync(project, 0, ct).GetAsyncEnumerator(ct);
        for (long seq = 1; seq <= 128; seq++)
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(seq, stream.Current.ServerSeq);
        }
        Assert.Null(reader.Database.CurrentTransaction);

        await Rotations.RotateAsync(new(writer, new(writer, database.DeliveryGate)), owner.Id, device, project, 131, ct);
        Assert.Equal(130, await Cleanup(writer).PruneAsync(ct));

        var error = await Assert.ThrowsAsync<ConflictException>(async () => await stream.MoveNextAsync());
        Assert.Contains("It now starts at update 131.", error.Message, StringComparison.Ordinal);
        var restarted = await new UpdateLog(reader).ReadAfterAsync(project, 130, ct).ToListAsync(ct);
        Assert.Equal(131, Assert.Single(restarted).ServerSeq);
    }

    [Fact]
    public async Task RotationBetweenValidationAndPageReadUsesTheValidatedSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        await using var writer = database.CreateContext();
        await Rotations.AuthorizeAsync(writer, project, device, ct);
        for (ulong seq = 1; seq <= 2; seq++)
            await new UpdateLog(writer).AppendAsync(owner.Id, Rotations.Update(project, device, seq, 0), ct);
        var rotate = new BeforeQuery("FROM updates AS", 1, async () =>
        {
            await Rotations.RotateAsync(new(writer, new(writer, database.DeliveryGate)), owner.Id, device, project, 3, ct);
            Assert.Equal(2, await Cleanup(writer).PruneAsync(ct));
        });
        await using var reader = database.CreateContext(rotate);

        var caughtUp = await new UpdateLog(reader).ReadAfterAsync(project, 0, ct).ToListAsync(ct);

        Assert.True(rotate.Fired);
        Assert.Equal(new[] { 1L, 2L }, caughtUp.Select(update => update.ServerSeq));
        await Assert.ThrowsAsync<ConflictException>(() => new UpdateLog(reader).ReadAfterAsync(project, 0, ct).ToListAsync(ct).AsTask());
    }

    [Fact]
    public async Task RotationOvertakingAHubStreamReturnsAClearCheckpointError()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        await using var writer = database.CreateContext();
        await Rotations.AuthorizeAsync(writer, project, device, ct);
        for (ulong seq = 1; seq <= 130; seq++)
            await new UpdateLog(writer).AppendAsync(owner.Id, Rotations.Update(project, device, seq, 0), ct);
        var rotate = new BeforeQuery("SELECT p.history_starts_at", 3, async () =>
        {
            await Rotations.RotateAsync(new(writer, new(writer, database.DeliveryGate)), owner.Id, device, project, 131, ct);
            Assert.Equal(130, await Cleanup(writer).PruneAsync(ct));
        });
        await using var server = new SyncServer(database, commands: rotate);
        await using var hub = server.CreateHubConnection(TestTokens.Create(owner.Subject), device);
        await hub.StartAsync(ct);
        var received = new List<long>();

        var error = await Assert.ThrowsAsync<HubException>(async () =>
        {
            await foreach (var update in hub.StreamAsync<SequencedEnvelope>("CatchUp", project, 0L, ct))
                received.Add(update.ServerSeq);
        });

        Assert.True(rotate.Fired);
        Assert.Equal(Enumerable.Range(1, 128).Select(seq => (long)seq), received);
        Assert.Contains(Protocol.HistoryReplaced + " It now starts at update 131.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PruningKeepsExactRetryReceiptsAndRejectsReusedSequencesUntilProjectDeletion()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        await using var db = database.CreateContext();
        await Rotations.AuthorizeAsync(db, project, device, ct);
        var log = new UpdateLog(db);
        var original = Rotations.Update(project, device, 1, 0);
        Assert.Equal(1, await log.AppendAsync(owner.Id, original, ct));
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        await Rotations.RotateAsync(epochs, owner.Id, device, project, 2, ct);
        Assert.Equal(1, await Cleanup(db).PruneAsync(ct));
        Assert.Equal(0, await Cleanup(db).PruneAsync(ct));

        await using (var retryDb = database.CreateContext())
        {
            var retries = new UpdateLog(retryDb);
            Assert.Equal(1, await retries.AppendAsync(owner.Id, original, ct));
            var changed = new[]
            {
                original with { Header = (EnvelopeHeader.Parse(original.Header) with { ItemId = Guid.NewGuid() }).ToBytes() },
                original with { Nonce = Flip(original.Nonce) },
                original with { Ciphertext = Flip(original.Ciphertext) },
                original with { Signature = Flip(original.Signature) },
                Rotations.Update(project, device, 1, 1)
            };
            foreach (var envelope in changed)
            {
                var error = await Assert.ThrowsAsync<PushRejectedException>(() => retries.AppendAsync(owner.Id, envelope, ct));
                Assert.Contains("already used for a different update", error.Message, StringComparison.Ordinal);
            }
        }
        var receipt = await db.UpdateReceipts.SingleAsync(value => value.ProjectId == project, ct);
        Assert.Equal(1, receipt.ServerSeq);
        Assert.All(new[] { receipt.HeaderHash, receipt.NonceHash, receipt.CiphertextHash, receipt.SignatureHash }, hash => Assert.Equal(32, hash.Length));
        Assert.Equal(new[] { 2L }, await db.Updates.Where(value => value.ProjectId == project).Select(value => value.ServerSeq).ToListAsync(ct));
        Assert.Equal(2, await db.Projects.Where(value => value.Id == project).Select(value => value.LastServerSeq).SingleAsync(ct));

        await Assert.ThrowsAsync<ConflictException>(() => Rotations.RotateAsync(epochs, owner.Id, device, project, 1, ct));
        await new ProjectAccess(db, database.DeliveryGate).DeleteAsync(owner.Id, project, ct);
        Assert.False(await db.UpdateReceipts.AnyAsync(value => value.ProjectId == project, ct));
    }

    [Fact]
    public async Task CleanupWaitsForProjectDeletionWithoutHoldingUpdateLocks()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        await using var setup = database.CreateContext();
        await Rotations.AuthorizeAsync(setup, project, device, ct);
        await new UpdateLog(setup).AppendAsync(owner.Id, Rotations.Update(project, device, 1, 0), ct);
        await Rotations.RotateAsync(new(setup, new(setup, database.DeliveryGate)), owner.Id, device, project, 2, ct);
        var projectLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeDeletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = new BeforeQuery("DELETE FROM updates", 1, async () =>
        {
            projectLocked.SetResult();
            await resumeDeletion.Task.WaitAsync(ct);
        });
        await using var deleting = database.CreateContext(pause);
        await using var cleaning = database.CreateContext();
        await deleting.Database.OpenConnectionAsync(ct);
        await cleaning.Database.OpenConnectionAsync(ct);
        var deletingPid = await deleting.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
        var cleaningPid = await cleaning.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
        var deletion = new ProjectAccess(deleting, database.DeliveryGate).DeleteAsync(owner.Id, project, ct);
        try
        {
            await projectLocked.Task.WaitAsync(ct);
            var pruning = Cleanup(cleaning).PruneAsync(ct);
            while (!await setup.Database.SqlQuery<bool>(
                $"SELECT {deletingPid} = ANY(pg_blocking_pids({cleaningPid})) AS \"Value\"").SingleAsync(ct))
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);

            resumeDeletion.SetResult();
            await Task.WhenAll(deletion, pruning);
            Assert.Equal(0, await pruning);
            Assert.False(await setup.Updates.AnyAsync(value => value.ProjectId == project, ct));
            Assert.False(await setup.UpdateReceipts.AnyAsync(value => value.ProjectId == project, ct));
        }
        finally { resumeDeletion.TrySetResult(); }
    }

    private static EphemeralStateCleanup Cleanup(SyncDbContext db) =>
        new(db, Options.Create(new UpdateHistoryOptions { CompactionGracePeriod = TimeSpan.Zero }));

    private static byte[] Flip(byte[] bytes)
    {
        var changed = bytes.ToArray();
        changed[0] ^= 1;
        return changed;
    }

    private sealed class BeforeQuery(string fragment, int occurrence, Func<Task> action) : DbCommandInterceptor
    {
        private int count;
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await BeforeAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await BeforeAsync(command);
            return result;
        }

        private async Task BeforeAsync(DbCommand command)
        {
            if (command.CommandText.Contains(fragment, StringComparison.Ordinal) && Interlocked.Increment(ref count) == occurrence)
            {
                Fired = true;
                await action();
            }
        }
    }
}
