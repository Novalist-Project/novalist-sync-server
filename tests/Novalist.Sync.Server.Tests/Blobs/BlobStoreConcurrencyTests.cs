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

public sealed class BlobStoreConcurrencyTests(DatabaseFixture database)
{
    [Theory]
    [InlineData("conflict")]
    [InlineData("retry")]
    [InlineData("quota")]
    public async Task DatabaseLockSerializesCompetingStoresWithoutProcessLocalGates(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        var pause = new SqlCommandPause("INSERT INTO blob_chunks");
        await using var firstDb = database.CreateContext(pause);
        await using var secondDb = database.CreateContext();
        await secondDb.Database.OpenConnectionAsync(ct);
        var process = ((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
        var options = Options.Create(new BlobStorageOptions { MaxProjectBytes = 32 });
        var blob = Guid.NewGuid();
        var chunk = new BlobChunk(0, RandomNumberGenerator.GetBytes(32));
        var competing = scenario switch
        {
            "retry" => chunk,
            "quota" => chunk with { Index = 1 },
            _ => chunk with { Ciphertext = RandomNumberGenerator.GetBytes(32) }
        };
        var first = new BlobStore(firstDb, options).PutAsync(project, blob, chunk, ct);
        Task<Exception?> second;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            second = Record.ExceptionAsync(() => new BlobStore(secondDb, options).PutAsync(project, blob, competing, ct)).AsTask();
            await DatabaseLocks.WaitUntilBlockedAsync(database, process, ct);
        }
        finally { pause.Release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var error = await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
        if (scenario == "retry") Assert.Null(error);
        else Assert.Contains(scenario == "quota" ? "quota" : "cannot change", Assert.IsType<ConflictException>(error).Message);
        await using var verify = database.CreateContext();
        var stored = await verify.BlobChunks.SingleAsync(value => value.ProjectId == project, ct);
        Assert.Equal(chunk.Ciphertext, stored.Ciphertext);
        Assert.Equal(32, await verify.Projects.Where(value => value.Id == project).Select(value => value.BlobBytesStored).SingleAsync(ct));
    }

    [Fact]
    public async Task CancelledUploadRollsBackTheChunkAndUsageTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        var pause = new SqlCommandPause("UPDATE projects");
        await using var db = database.CreateContext(pause);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var options = Options.Create(new BlobStorageOptions { MaxProjectBytes = 32 });
        var blob = Guid.NewGuid();
        var chunk = new BlobChunk(0, RandomNumberGenerator.GetBytes(32));
        var upload = new BlobStore(db, options).PutAsync(project, blob, chunk, cancelled.Token);
        try { await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        finally { await cancelled.CancelAsync(); pause.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        await using var verify = database.CreateContext();
        Assert.False(await verify.BlobChunks.AnyAsync(value => value.ProjectId == project, ct));
        Assert.Equal(0, await verify.Projects.Where(value => value.Id == project).Select(value => value.BlobBytesStored).SingleAsync(ct));
        await new BlobStore(verify, options).PutAsync(project, blob, chunk, ct);
        Assert.Equal(32, await verify.Projects.Where(value => value.Id == project).Select(value => value.BlobBytesStored).SingleAsync(ct));
    }
}
