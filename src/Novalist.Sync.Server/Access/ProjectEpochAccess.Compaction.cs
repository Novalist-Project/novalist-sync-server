using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Access;

// A compaction replaces the history before the reserved position with a checkpoint at the current epoch.
// Unlike a rotation it changes no keys, grants, invitations or field leases, so any writer may run it.
public sealed partial class ProjectEpochAccess
{
    public async Task<ProjectEpochStatus> ReserveCompactionAsync(Guid user, Guid device, Guid projectId, Guid compaction, CancellationToken ct)
    {
        if (compaction == Guid.Empty) throw new FormatException("A compaction identifier is required.");
        await using var transaction = await AccountAccessLock.AcquireAsync(db, user, legacyAccount: false, ct, shared: true);
        await EnsureDeviceAsync(user, device, ct);
        var project = await LockProjectAsync(projectId, ct);
        await projects.EnsureCanWriteAsync(user, projectId, ct);
        var now = await DatabaseTimeAsync(ct);
        await db.ProjectEpochStages.Where(stage => stage.ProjectId == projectId).Where(ExpiredAt(now)).ExecuteDeleteAsync(ct);
        await EnsureReservationAvailableAsync(projectId, compaction, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO project_epoch_stages (id, project_id, device_id, previous_epoch, previous_server_seq, created_at, last_activity_at, count, compaction)
            VALUES ({compaction}, {projectId}, {device}, {project.CurrentEpoch}, {project.LastServerSeq}, {now}, {now}, 0, TRUE)
            ON CONFLICT (id) DO NOTHING
            """, ct);
        var reserved = await LockStageAsync(compaction, device, ct);
        if (reserved.ProjectId != projectId || !reserved.Compaction) throw new ConflictException("The compaction identifier is already in use.");
        CheckBoundary(project, reserved.PreviousEpoch, reserved.PreviousServerSeq);
        await transaction.CommitAsync(ct);
        return new((uint)reserved.PreviousEpoch, reserved.PreviousServerSeq);
    }

    public async Task<(Guid ProjectId, long HistoryStartsAt)> CommitCompactionAsync(Guid user, Guid device, Guid compaction, CancellationToken ct)
    {
        var projectId = await db.ProjectEpochStages.AsNoTracking().Where(value => value.Id == compaction && value.Compaction)
            .Select(value => (Guid?)value.ProjectId).SingleOrDefaultAsync(ct)
            ?? throw new ConflictException("The history compaction is unavailable.");
        await projects.EnsureCanWriteAsync(user, projectId, ct);
        await using var delivery = await projects.PauseDeliveryAsync(projectId, ct);
        await using var transaction = await AccountAccessLock.AcquireManyAsync(db, [user], ct);
        await EnsureDeviceAsync(user, device, ct);
        var project = await LockProjectAsync(projectId, ct);
        var stage = await LockStageAsync(compaction, device, ct, projectId);
        if (!stage.Compaction) throw new ConflictException("The history compaction is unavailable.");
        await projects.EnsureCanWriteAsync(user, projectId, ct);
        if (project.CurrentEpoch != stage.PreviousEpoch || project.LastServerSeq != stage.PreviousServerSeq)
            throw new ConflictException("The project changed during compaction. Try again later.");
        if (stage.Count == 0) throw new ConflictException("Upload the fresh encrypted checkpoint first.");
        // The checkpoint lands right after the reserved position, so history starts with it.
        var historyStartsAt = stage.PreviousServerSeq + 1;
        await db.Projects.Where(value => value.Id == projectId)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.HistoryStartsAt, historyStartsAt), ct);
        await CopyCheckpointAsync(user, stage, ct);
        var checkpointBytes = await StoredBytesAsync(projectId, historyStartsAt, ct);
        await db.ProjectEpochStages.Where(value => value.Id == stage.Id).ExecuteDeleteAsync(ct);
        var committedAt = await DatabaseTimeAsync(ct);
        db.ProjectHistoryBoundaries.Add(new() { ProjectId = projectId, FirstServerSeq = historyStartsAt, CreatedAt = committedAt, CheckpointBytes = checkpointBytes });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        log.LogInformation(
            "Compacted history of project {ProjectId}: history starts at {HistoryStartsAt}, checkpoint {EnvelopeCount} envelopes / {CheckpointBytes} bytes, staged for {StagedSeconds:F1}s.",
            projectId, historyStartsAt, stage.Count, checkpointBytes, (committedAt - stage.CreatedAt).TotalSeconds);
        return (projectId, historyStartsAt);
    }

    public async Task<ProjectHistoryStatus> HistoryAsync(Guid user, Guid projectId, CancellationToken ct)
    {
        await projects.EnsureCanReadAsync(user, projectId, ct);
        var project = await db.Projects.AsNoTracking().Where(value => value.Id == projectId)
            .Select(value => new { value.HistoryStartsAt, value.LastServerSeq }).SingleAsync(ct);
        var boundary = await db.ProjectHistoryBoundaries.AsNoTracking().Where(value => value.ProjectId == projectId)
            .OrderByDescending(value => value.FirstServerSeq).FirstOrDefaultAsync(ct);
        return new(project.HistoryStartsAt, project.LastServerSeq, await StoredBytesAsync(projectId, project.HistoryStartsAt, ct),
            boundary?.CheckpointBytes, boundary?.CreatedAt);
    }

    private Task<long> StoredBytesAsync(Guid projectId, long fromServerSeq, CancellationToken ct) =>
        db.Updates.Where(update => update.ProjectId == projectId && update.ServerSeq >= fromServerSeq)
            .SumAsync(update => (long)update.Header.Length + update.Nonce.Length + update.Ciphertext.Length + update.Signature.Length, ct);
}
