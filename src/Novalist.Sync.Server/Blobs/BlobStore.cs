using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Blobs;

public sealed class BlobStore(SyncDbContext db, IOptions<BlobStorageOptions> options)
{
    internal static void ValidateIdentity(Guid projectId, Guid blobId, int index)
    {
        if (projectId == Guid.Empty || blobId == Guid.Empty || index < 0)
            throw new FormatException("Nonempty project and blob identities and a nonnegative chunk index are required.");
    }

    internal async Task PutAsync(Guid projectId, Guid blobId, BlobChunk chunk, CancellationToken ct)
    {
        ValidateIdentity(projectId, blobId, chunk.Index);
        if (chunk.Ciphertext is not { Length: >= BlobTransfer.MinimumCiphertextSize and <= BlobTransfer.MaximumCiphertextSize })
            throw new FormatException("Blob ciphertext must contain between 16 and 65552 bytes.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var project = await db.Projects.FromSqlInterpolated($"SELECT * FROM projects WHERE id = {projectId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new AccessDeniedException("The project is not available.");
        var stored = await GetAsync(projectId, blobId, chunk.Index, ct);
        if (stored is not null)
        {
            if (!stored.Ciphertext.AsSpan().SequenceEqual(chunk.Ciphertext))
                throw new ConflictException("A blob chunk cannot change. Use a new blob identity for changed content.");
            return;
        }
        if (project.BlobBytesStored > options.Value.MaxProjectBytes - chunk.Ciphertext.Length)
            throw new ConflictException("The project blob storage quota has been reached.");
        // A new chunk shows the upload is still in progress, so it clears any collection marks.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO blobs (project_id, blob_id, bytes_stored)
            VALUES ({projectId}, {blobId}, {(long)chunk.Ciphertext.Length})
            ON CONFLICT (project_id, blob_id) DO UPDATE
            SET bytes_stored = blobs.bytes_stored + EXCLUDED.bytes_stored, unreferenced_since = NULL, collectable_at = NULL
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO blob_chunks (project_id, blob_id, index, ciphertext)
            VALUES ({projectId}, {blobId}, {chunk.Index}, {chunk.Ciphertext})
            """, ct);
        await db.Projects.Where(value => value.Id == projectId).ExecuteUpdateAsync(
            set => set.SetProperty(value => value.BlobBytesStored, value => value.BlobBytesStored + chunk.Ciphertext.Length), ct);
        await transaction.CommitAsync(ct);
    }

    // Buffers a report before any lock is taken, so a slow sender cannot hold up uploads or revocation.
    internal static async Task<Guid[]> ReadReferencesAsync(IAsyncEnumerable<Guid> referencedBlobIds, CancellationToken ct)
    {
        var referenced = new HashSet<Guid>();
        await foreach (var blobId in referencedBlobIds.WithCancellation(ct))
        {
            if (blobId == Guid.Empty) throw new FormatException("Reported blob identities must be nonempty.");
            if (referenced.Add(blobId) && referenced.Count > BlobTransfer.MaximumReportedReferences)
                throw new FormatException($"A reference report may contain at most {BlobTransfer.MaximumReportedReferences} blobs.");
        }
        return [.. referenced];
    }

    // Applies one complete report: reported blobs lose their marks, omitted blobs are marked unreferenced,
    // and blobs already omitted by a report at least one grace period earlier become collectable.
    // IDs of blobs that have no chunks yet are ignored.
    internal async Task ReportReferencesAsync(Guid projectId, Guid[] referencedBlobIds, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        _ = await db.Projects.FromSqlInterpolated($"SELECT * FROM projects WHERE id = {projectId} FOR NO KEY UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new AccessDeniedException("The project is not available.");
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var cutoff = now - options.Value.UnreferencedGracePeriod;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE blobs AS b
            SET unreferenced_since = CASE WHEN r.blob_id IS NULL THEN COALESCE(b.unreferenced_since, {now}) END,
                collectable_at = CASE WHEN r.blob_id IS NULL AND b.unreferenced_since <= {cutoff}
                    THEN COALESCE(b.collectable_at, {now}) END
            FROM blobs AS s LEFT JOIN unnest({referencedBlobIds}::uuid[]) AS r(blob_id) ON r.blob_id = s.blob_id
            WHERE s.project_id = {projectId} AND b.project_id = s.project_id AND b.blob_id = s.blob_id
            """, ct);
        await transaction.CommitAsync(ct);
    }

    internal Task<BlobChunk?> GetAsync(Guid projectId, Guid blobId, int chunkIndex, CancellationToken ct) =>
        db.BlobChunks.AsNoTracking()
            .Where(value => value.ProjectId == projectId && value.BlobId == blobId && value.Index == chunkIndex)
            .Select(value => new BlobChunk(value.Index, value.Ciphertext))
            .SingleOrDefaultAsync(ct);
}
