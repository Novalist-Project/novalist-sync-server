using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Blobs;

public sealed class BlobCollector(SyncDbContext db, IOptions<BlobStorageOptions> options)
{
    // Deletes collectable blobs and their chunks and returns their storage to the project quota.
    // Returns how many blobs were deleted.
    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var total = 0;
        for (var batch = 0; batch < options.Value.CollectionMaxBatchesPerRun; batch++)
        {
            var deleted = await CollectBatchAsync(ct);
            total += deleted;
            if (deleted < options.Value.CollectionBatchSize) break;
        }
        return total;
    }

    private async Task<int> CollectBatchAsync(CancellationToken ct)
    {
        var size = options.Value.CollectionBatchSize;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Lock the projects first, as uploads, reports and project deletion do. The delete runs as a
        // separate statement so it sees reports and uploads that committed while we waited for the lock.
        var projectIds = await db.Database.SqlQuery<Guid>($"""
            SELECT p.id AS "Value" FROM projects p
            WHERE EXISTS (SELECT 1 FROM blobs b WHERE b.project_id = p.id AND b.collectable_at IS NOT NULL)
            ORDER BY p.id
            LIMIT {size}
            FOR NO KEY UPDATE OF p
            """).ToArrayAsync(ct);
        if (projectIds.Length == 0) return 0;
        var removed = await db.Database.SqlQuery<long>($"""
            WITH due AS (
                SELECT project_id, blob_id FROM blobs
                WHERE project_id = ANY({projectIds}::uuid[]) AND collectable_at IS NOT NULL
                ORDER BY project_id, blob_id
                LIMIT {size}
            ), removed AS (
                DELETE FROM blobs b USING due d
                WHERE b.project_id = d.project_id AND b.blob_id = d.blob_id AND b.collectable_at IS NOT NULL
                RETURNING b.project_id, b.bytes_stored
            ), usage AS (
                SELECT project_id, sum(bytes_stored) AS bytes, count(*) AS blobs FROM removed GROUP BY project_id
            )
            UPDATE projects p SET blob_bytes_stored = p.blob_bytes_stored - u.bytes
            FROM usage u WHERE p.id = u.project_id
            RETURNING u.blobs AS "Value"
            """).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return (int)removed.Sum();
    }
}
