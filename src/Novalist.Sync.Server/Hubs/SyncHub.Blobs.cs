using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Blobs;

namespace Novalist.Sync.Server.Hubs;

public sealed partial class SyncHub
{
    public async Task UploadBlobChunk(Guid projectId, Guid blobId, BlobChunk chunk)
    {
        var ct = Context.ConnectionAborted;
        await using var access = await devices.PauseAccessChangesAsync(UserId, ct);
        await using var delivery = await projects.PauseDeliveryAsync(projectId, ct);
        await EnsureDeviceActiveAsync(ct);
        await projects.EnsureCanWriteAsync(UserId, projectId, ct);
        if (chunk is null) throw new FormatException("A blob chunk is required.");
        await blobs.PutAsync(projectId, blobId, chunk, ct);
        logger.LogInformation("Device {DeviceId} uploaded blob {BlobId} chunk {ChunkIndex} to project {ProjectId} ({Size} bytes).",
            DeviceId, blobId, chunk.Index, projectId, chunk.Ciphertext.Length);
    }

    public async Task<BlobChunk?> DownloadBlobChunk(Guid projectId, Guid blobId, int chunkIndex)
    {
        var ct = Context.ConnectionAborted;
        await using var access = await devices.PauseAccessChangesAsync(UserId, ct);
        await using var delivery = await projects.PauseDeliveryAsync(projectId, ct);
        await EnsureDeviceActiveAsync(ct);
        await projects.EnsureCanReadAsync(UserId, projectId, ct);
        BlobStore.ValidateIdentity(projectId, blobId, chunkIndex);
        return await blobs.GetAsync(projectId, blobId, chunkIndex, ct);
    }
}
