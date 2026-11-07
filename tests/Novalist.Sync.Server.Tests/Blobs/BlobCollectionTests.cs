using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Blobs;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Npgsql;

namespace Novalist.Sync.Server.Tests.Blobs;

public sealed class BlobCollectionTests(DatabaseFixture database)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IOptions<BlobStorageOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new BlobStorageOptions { UnreferencedGracePeriod = TimeSpan.FromDays(30) });

    [Fact]
    public async Task OmittedBlobIsCollectedOnlyAfterALaterReportConfirmsItPastTheGracePeriod()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        await using var db = database.CreateContext(clock);
        var store = new BlobStore(db, Options);
        var collector = new BlobCollector(db, Options);
        var kept = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        await store.PutAsync(project, kept, new(0, RandomNumberGenerator.GetBytes(32)), ct);
        await store.PutAsync(project, dropped, new(0, RandomNumberGenerator.GetBytes(64)), ct);
        await store.PutAsync(project, dropped, new(1, RandomNumberGenerator.GetBytes(16)), ct);

        await store.ReportReferencesAsync(project, [kept, Guid.NewGuid()], ct);
        Assert.Equal((Start, (DateTimeOffset?)null), await MarksAsync(db, project, dropped, ct));
        Assert.Equal((null, null), await MarksAsync(db, project, kept, ct));
        Assert.Equal(0, await collector.CollectAsync(ct));

        // Without another report, time alone never makes a blob collectable, so dormant projects keep everything.
        clock.Now = Start.AddDays(90);
        Assert.Equal(0, await collector.CollectAsync(ct));

        clock.Now = Start.AddDays(29);
        await store.ReportReferencesAsync(project, [kept], ct);
        Assert.Equal((Start, (DateTimeOffset?)null), await MarksAsync(db, project, dropped, ct));
        Assert.Equal(0, await collector.CollectAsync(ct));

        clock.Now = Start.AddDays(30);
        await store.ReportReferencesAsync(project, [kept], ct);
        Assert.Equal((Start, Start.AddDays(30)), await MarksAsync(db, project, dropped, ct));
        Assert.Equal(1, await collector.CollectAsync(ct));
        Assert.Equal(0, await collector.CollectAsync(ct));

        Assert.False(await db.Blobs.AnyAsync(value => value.ProjectId == project && value.BlobId == dropped, ct));
        Assert.False(await db.BlobChunks.AnyAsync(value => value.ProjectId == project && value.BlobId == dropped, ct));
        Assert.NotNull(await store.GetAsync(project, kept, 0, ct));
        Assert.Equal(32, await StoredBytesAsync(db, project, ct));
    }

    [Theory]
    [InlineData("report")]
    [InlineData("upload")]
    public async Task ReferencingOrResumingABlobClearsItsMarks(string action)
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        await using var db = database.CreateContext(clock);
        var store = new BlobStore(db, Options);
        var blob = Guid.NewGuid();
        await store.PutAsync(project, blob, new(0, RandomNumberGenerator.GetBytes(16)), ct);
        await store.ReportReferencesAsync(project, [], ct);
        clock.Now = Start.AddDays(31);
        await store.ReportReferencesAsync(project, [], ct);
        Assert.NotNull((await MarksAsync(db, project, blob, ct)).CollectableAt);

        if (action == "report") await store.ReportReferencesAsync(project, [blob], ct);
        else await store.PutAsync(project, blob, new(1, RandomNumberGenerator.GetBytes(16)), ct);
        Assert.Equal((null, null), await MarksAsync(db, project, blob, ct));

        // The grace period restarts from the next omission.
        clock.Now = Start.AddDays(40);
        await store.ReportReferencesAsync(project, [], ct);
        clock.Now = Start.AddDays(69);
        await store.ReportReferencesAsync(project, [], ct);
        Assert.Equal(0, await new BlobCollector(db, Options).CollectAsync(ct));
        Assert.NotNull(await store.GetAsync(project, blob, 0, ct));
    }

    [Fact]
    public async Task ReportsAreScopedToTheirProject()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await CreateProjectAsync();
        var second = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        await using var db = database.CreateContext(clock);
        var store = new BlobStore(db, Options);
        var blob = Guid.NewGuid();
        await store.PutAsync(first, blob, new(0, RandomNumberGenerator.GetBytes(16)), ct);
        await store.PutAsync(second, blob, new(0, RandomNumberGenerator.GetBytes(16)), ct);
        await store.ReportReferencesAsync(first, [], ct);
        clock.Now = Start.AddDays(31);
        await store.ReportReferencesAsync(first, [], ct);
        await store.ReportReferencesAsync(second, [], ct);
        Assert.Equal(1, await new BlobCollector(db, Options).CollectAsync(ct));
        Assert.Null(await store.GetAsync(first, blob, 0, ct));
        Assert.NotNull(await store.GetAsync(second, blob, 0, ct));
        Assert.Equal(16, await StoredBytesAsync(db, second, ct));
        Assert.Equal(0, await StoredBytesAsync(db, first, ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ReportReferencesAsync(Guid.NewGuid(), [], ct));
    }

    [Fact]
    public async Task CollectionRunsInBoundedBatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateProjectAsync();
        var clock = new DatabaseTimeOverride(Start);
        await using var db = database.CreateContext(clock);
        var store = new BlobStore(db, Options);
        for (var blob = 0; blob < 5; blob++) await store.PutAsync(project, Guid.NewGuid(), new(0, new byte[16]), ct);
        await store.ReportReferencesAsync(project, [], ct);
        clock.Now = Start.AddDays(31);
        await store.ReportReferencesAsync(project, [], ct);
        var options = Microsoft.Extensions.Options.Options.Create(new BlobStorageOptions { CollectionBatchSize = 2, CollectionMaxBatchesPerRun = 2 });
        var collector = new BlobCollector(db, options);
        Assert.Equal(4, await collector.CollectAsync(ct));
        Assert.Equal(1, await collector.CollectAsync(ct));
        Assert.Equal(0, await StoredBytesAsync(db, project, ct));
    }

    [Theory]
    [InlineData("report")]
    [InlineData("upload")]
    public async Task CollectionWaitsForAConcurrentReferenceAndKeepsTheBlob(string action)
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateProjectAsync();
        var blob = Guid.NewGuid();
        await using (var setup = database.CreateContext(new DatabaseTimeOverride(Start.AddDays(-31))))
        {
            var setupStore = new BlobStore(setup, Options);
            await setupStore.PutAsync(project, blob, new(0, RandomNumberGenerator.GetBytes(16)), ct);
            await setupStore.ReportReferencesAsync(project, [], ct);
        }
        await using (var setup = database.CreateContext(new DatabaseTimeOverride(Start)))
        {
            await new BlobStore(setup, Options).ReportReferencesAsync(project, [], ct);
            Assert.NotNull((await MarksAsync(setup, project, blob, ct)).CollectableAt);
        }

        var pause = new SqlCommandPause(action == "report" ? "UPDATE blobs AS b" : "INSERT INTO blob_chunks");
        await using var referencing = database.CreateContext(pause);
        await using var collecting = database.CreateContext();
        await collecting.Database.OpenConnectionAsync(ct);
        var process = ((NpgsqlConnection)collecting.Database.GetDbConnection()).ProcessID;
        var store = new BlobStore(referencing, Options);
        var reference = action == "report"
            ? store.ReportReferencesAsync(project, [blob], ct)
            : store.PutAsync(project, blob, new(1, RandomNumberGenerator.GetBytes(16)), ct);
        Task<int> collection;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            collection = new BlobCollector(collecting, Options).CollectAsync(ct);
            await DatabaseLocks.WaitUntilBlockedAsync(database, process, ct);
        }
        finally { pause.Release.TrySetResult(); }
        await reference.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(0, await collection.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await using var verify = database.CreateContext();
        Assert.Equal((null, null), await MarksAsync(verify, project, blob, ct));
        Assert.NotNull(await new BlobStore(verify, Options).GetAsync(project, blob, 0, ct));
        Assert.Equal(action == "report" ? 16 : 32, await StoredBytesAsync(verify, project, ct));
    }

    [Fact]
    public async Task ReportReaderDeduplicatesAndBoundsDistinctIdentities()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(0, BlobTransfer.MaximumReportedReferences).Select(_ => Guid.NewGuid()).ToArray();
        var read = await BlobStore.ReadReferencesAsync(Stream([.. ids, .. ids]), ct);
        Assert.Equal(BlobTransfer.MaximumReportedReferences, read.Length);
        var error = await Assert.ThrowsAsync<FormatException>(() => BlobStore.ReadReferencesAsync(Stream([.. ids, Guid.NewGuid()]), ct));
        Assert.Contains("at most", error.Message);
        await Assert.ThrowsAsync<FormatException>(() => BlobStore.ReadReferencesAsync(Stream([Guid.Empty]), ct));
    }

    private static async IAsyncEnumerable<Guid> Stream(Guid[] ids)
    {
        foreach (var id in ids) yield return id;
        await Task.CompletedTask;
    }

    private async Task<Guid> CreateProjectAsync()
    {
        var user = await database.CreateUserAsync();
        return await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
    }

    private static async Task<(DateTimeOffset? UnreferencedSince, DateTimeOffset? CollectableAt)> MarksAsync(
        SyncDbContext db, Guid project, Guid blob, CancellationToken ct)
    {
        var stored = await db.Blobs.AsNoTracking().SingleAsync(value => value.ProjectId == project && value.BlobId == blob, ct);
        return (stored.UnreferencedSince, stored.CollectableAt);
    }

    private static Task<long> StoredBytesAsync(SyncDbContext db, Guid project, CancellationToken ct) =>
        db.Projects.Where(value => value.Id == project).Select(value => value.BlobBytesStored).SingleAsync(ct);
}
