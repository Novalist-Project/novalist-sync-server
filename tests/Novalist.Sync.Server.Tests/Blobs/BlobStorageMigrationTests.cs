using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Blobs;
using Novalist.Sync.Server.Data;
using Npgsql;

namespace Novalist.Sync.Server.Tests.Blobs;

public sealed class BlobStorageMigrationTests(DatabaseFixture database)
{
    [Fact]
    public async Task QuotaMigrationBackfillsExistingChunksAndRollbackPreservesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Database = $"novalist_blob_upgrade_test_{Guid.NewGuid():N}"
        };
        await using var db = new SyncDbContext(new DbContextOptionsBuilder<SyncDbContext>()
            .UseNpgsql(connection.ConnectionString).UseSnakeCaseNamingConvention().Options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261107180309_BlobChunks", ct);
            var project = Guid.NewGuid();
            var empty = Guid.NewGuid();
            var blob = Guid.NewGuid();
            var first = new BlobChunk(0, RandomNumberGenerator.GetBytes(32));
            var last = RandomNumberGenerator.GetBytes(16);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO projects (id, current_epoch, epoch_started_at, last_server_seq, created_at)
                VALUES ({project}, 0, CURRENT_TIMESTAMP, 0, CURRENT_TIMESTAMP),
                    ({empty}, 0, CURRENT_TIMESTAMP, 0, CURRENT_TIMESTAMP);
                INSERT INTO blob_chunks (project_id, blob_id, index, ciphertext)
                VALUES ({project}, {blob}, 0, {first.Ciphertext}), ({project}, {blob}, 1, {last});
                """, ct);
            for (var pass = 0; pass < 2; pass++)
            {
                await db.Database.MigrateAsync(ct);
                Assert.Equal(48, await db.Projects.Where(value => value.Id == project).Select(value => value.BlobBytesStored).SingleAsync(ct));
                Assert.Equal(0, await db.Projects.Where(value => value.Id == empty).Select(value => value.BlobBytesStored).SingleAsync(ct));
                var store = new BlobStore(db, Options.Create(new BlobStorageOptions { MaxProjectBytes = 32 }));
                await store.PutAsync(project, blob, first, ct);
                Assert.Equal(first.Ciphertext, (await store.GetAsync(project, blob, 0, ct))!.Ciphertext);
                Assert.Equal(last, (await store.GetAsync(project, blob, 1, ct))!.Ciphertext);
                var error = await Assert.ThrowsAsync<ConflictException>(() => store.PutAsync(project, Guid.NewGuid(), new(0, last), ct));
                Assert.Contains("quota", error.Message);
                await db.GetService<IMigrator>().MigrateAsync("20261107180309_BlobChunks", ct);
                Assert.Equal(2, await db.BlobChunks.CountAsync(ct));
            }
        }
        finally { await db.Database.EnsureDeletedAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CollectionMigrationBackfillsBlobsFromChunksAndRollbackPreservesChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Database = $"novalist_blob_collection_test_{Guid.NewGuid():N}"
        };
        await using var db = new SyncDbContext(new DbContextOptionsBuilder<SyncDbContext>()
            .UseNpgsql(connection.ConnectionString).UseSnakeCaseNamingConvention().Options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261107182416_BlobStorageQuota", ct);
            var project = Guid.NewGuid();
            var large = Guid.NewGuid();
            var small = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO projects (id, current_epoch, epoch_started_at, last_server_seq, created_at, blob_bytes_stored)
                VALUES ({project}, 0, CURRENT_TIMESTAMP, 0, CURRENT_TIMESTAMP, 80);
                INSERT INTO blob_chunks (project_id, blob_id, index, ciphertext)
                VALUES ({project}, {large}, 0, {new byte[32]}), ({project}, {large}, 1, {new byte[32]}),
                    ({project}, {small}, 0, {new byte[16]});
                """, ct);
            for (var pass = 0; pass < 2; pass++)
            {
                await db.Database.MigrateAsync(ct);
                var blobs = await db.Blobs.AsNoTracking().ToDictionaryAsync(value => value.BlobId, ct);
                Assert.Equal(2, blobs.Count);
                Assert.Equal(64, blobs[large].BytesStored);
                Assert.Equal(16, blobs[small].BytesStored);
                Assert.All(blobs.Values, value => Assert.Null(value.UnreferencedSince));
                var store = new BlobStore(db, Options.Create(new BlobStorageOptions()));
                await store.PutAsync(project, small, new(1, new byte[16]), ct);
                Assert.Equal(32, await db.Blobs.Where(value => value.BlobId == small).Select(value => value.BytesStored).SingleAsync(ct));
                await db.GetService<IMigrator>().MigrateAsync("20261107182416_BlobStorageQuota", ct);
                Assert.Equal(4, await db.BlobChunks.CountAsync(ct));
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM blob_chunks WHERE blob_id = {small} AND index = 1", ct);
            }
        }
        finally { await db.Database.EnsureDeletedAsync(CancellationToken.None); }
    }
}
