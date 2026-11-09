using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public sealed partial class ProjectEpochAccess
{
    public async Task StageGrantAsync(Guid user, Guid device, Guid rotation, ProjectEpochGrant grant, CancellationToken ct)
    {
        ValidateGrant(grant);
        await using var transaction = await AccountAccessLock.AcquireAsync(db, user, legacyAccount: false, ct, shared: true);
        await EnsureDeviceAsync(user, device, ct);
        var stage = await LockStageForUploadAsync(rotation, device, ct);
        if (stage.Compaction) throw new ConflictException("A history compaction keeps the current keys and takes no grants.");
        await projects.EnsureIsOwnerAsync(user, stage.ProjectId, ct);
        var existing = await db.ProjectEpochDeviceGrants.FindAsync([rotation, grant.DeviceId], ct);
        if (existing is not null)
        {
            if (!existing.Certificate.AsSpan().SequenceEqual(grant.Certificate) || !existing.Payload.AsSpan().SequenceEqual(grant.Payload))
                throw new ConflictException("The staged key grant changed.");
            await transaction.CommitAsync(ct);
            return;
        }
        db.ProjectEpochDeviceGrants.Add(new()
        {
            RotationId = rotation,
            DeviceId = grant.DeviceId,
            Certificate = grant.Certificate.ToArray(),
            Payload = grant.Payload.ToArray()
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
