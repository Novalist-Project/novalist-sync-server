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
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO blob_chunks (project_id, blob_id, index, ciphertext)
            VALUES ({projectId}, {blobId}, {chunk.Index}, {chunk.Ciphertext})
            """, ct);
        await db.Projects.Where(value => value.Id == projectId).ExecuteUpdateAsync(
            set => set.SetProperty(value => value.BlobBytesStored, value => value.BlobBytesStored + chunk.Ciphertext.Length), ct);
        await transaction.CommitAsync(ct);
    }

    internal Task<BlobChunk?> GetAsync(Guid projectId, Guid blobId, int chunkIndex, CancellationToken ct) =>
        db.BlobChunks.AsNoTracking()
            .Where(value => value.ProjectId == projectId && value.BlobId == blobId && value.Index == chunkIndex)
            .Select(value => new BlobChunk(value.Index, value.Ciphertext))
            .SingleOrDefaultAsync(ct);
}
