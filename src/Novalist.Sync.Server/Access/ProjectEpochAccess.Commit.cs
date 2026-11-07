using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Access;

public sealed partial class ProjectEpochAccess
{
    public async Task<Guid> CommitAsync(Guid user, Guid device, CommitProjectEpoch request, CancellationToken ct,
        Func<Guid, Task>? onCommitted = null)
    {
        if (request.RemovedMemberId == user) throw new ConflictException("The owner cannot remove themselves.");
        var projectId = await db.ProjectEpochStages.AsNoTracking().Where(value => value.Id == request.RotationId).Select(value => (Guid?)value.ProjectId).SingleOrDefaultAsync(ct)
            ?? throw new ConflictException("The project transition is unavailable.");
        await projects.EnsureIsOwnerAsync(user, projectId, ct);
        await using var delivery = await projects.PauseDeliveryAsync(projectId, ct);
        await using var transaction = await AccountAccessLock.AcquireManyAsync(db,
            request.RemovedMemberId is { } removed ? [user, removed] : [user], ct);
        await EnsureDeviceAsync(user, device, ct);
        var project = await LockProjectAsync(projectId, ct);
        var stage = await LockStageAsync(request.RotationId, device, ct);
        if (stage.ProjectId != project.Id) throw new ConflictException("The project transition changed. Refresh and try again.");
        await projects.EnsureIsOwnerAsync(user, project.Id, ct);
        if (request.RemovedMemberId.HasValue &&
            !await db.Memberships.AnyAsync(value => value.ProjectId == project.Id && value.UserId == request.RemovedMemberId, ct))
            throw new ConflictException("The account being removed is not a member of this project.");
        CheckBoundary(project, stage.PreviousEpoch, stage.PreviousServerSeq);
        if (stage.Count == 0) throw new ConflictException("Upload the fresh encrypted checkpoint first.");
        var grants = request.Grants is { Count: > 0 } ? request.Grants :
            await db.ProjectEpochDeviceGrants.AsNoTracking().Where(value => value.RotationId == stage.Id)
                .Select(value => new ProjectEpochGrant(value.DeviceId, value.Certificate, value.Payload)).ToListAsync(ct);
        var members = (await DeviceQuery(project.Id).ToListAsync(ct)).Where(value => value.UserId != request.RemovedMemberId).ToArray();
        if (!members.Any(value => value.DeviceId == device) ||
            !members.Select(value => value.DeviceId).Order().SequenceEqual(grants.Select(value => value.DeviceId).Order()))
            throw new ConflictException("Key grants must include exactly the remaining registered devices.");
        foreach (var grant in grants)
        {
            ValidateGrant(grant);
            db.ProjectEpochKeyGrants.Add(new()
            {
                ProjectId = project.Id,
                DeviceId = grant.DeviceId,
                Epoch = stage.PreviousEpoch + 1,
                FirstServerSeq = stage.PreviousServerSeq + 1,
                GrantorDeviceId = device,
                Payload = grant.Payload.ToArray()
            });
            await db.ProjectDeviceAuthorizations.Where(value => value.ProjectId == project.Id && value.DeviceId == grant.DeviceId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.Epoch, stage.PreviousEpoch + 1).SetProperty(value => value.Certificate, grant.Certificate), ct);
        }
        var advanced = await db.Projects.Where(value => value.Id == project.Id && value.CurrentEpoch == stage.PreviousEpoch && value.LastServerSeq == stage.PreviousServerSeq)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.CurrentEpoch, stage.PreviousEpoch + 1)
                .SetProperty(value => value.EpochStartedAt, DateTimeOffset.UtcNow), ct);
        if (advanced != 1) throw new ConflictException("The project changed during key rotation. Refresh and try again.");
        await db.SaveChangesAsync(ct);
        await CopyCheckpointAsync(user, stage, ct);
        if (request.RemovedMemberId is { } memberId)
        {
            await db.Memberships.Where(value => value.ProjectId == project.Id && value.UserId == memberId).ExecuteDeleteAsync(ct);
            await db.ProjectDeviceAuthorizations.Where(value => value.ProjectId == project.Id && db.Devices.Any(d => d.Id == value.DeviceId && d.UserId == memberId)).ExecuteDeleteAsync(ct);
        }
        await db.Invitations.Where(value => value.ProjectId == project.Id).ExecuteUpdateAsync(set => set.SetProperty(value => value.ExpiresAt, DateTimeOffset.UnixEpoch), ct);
        await db.FieldLeases.Where(value => value.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await db.ProjectEpochStages.Where(value => value.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        if (onCommitted is not null) await onCommitted(project.Id);
        return project.Id;
    }

    private async Task CopyCheckpointAsync(Guid user, ProjectEpochStage stage, CancellationToken ct)
    {
        const int pageSize = 128;
        for (long after = 0; after < stage.Count; after += pageSize)
        {
            var page = await db.ProjectEpochEnvelopes.AsNoTracking().Where(value => value.RotationId == stage.Id && value.Index > after)
                .OrderBy(value => value.Index).Take(pageSize).ToListAsync(ct);
            foreach (var value in page)
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(value.Payload) ?? throw new FormatException("Missing checkpoint envelope.");
                if (await db.Updates.AnyAsync(update => update.ProjectId == stage.ProjectId &&
                    update.SenderDeviceId == stage.DeviceId && update.SenderSeq == value.SenderSeq, ct))
                    throw new ConflictException("The checkpoint sender sequence was already used.");
                await new UpdateLog(db).AppendAsync(user, envelope, ct);
            }
        }
    }
}
