using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public sealed partial class ProjectEpochAccess
{
    public async Task<ProjectEpochStatus> ReserveAsync(Guid user, Guid device, Guid projectId, Guid rotation, CancellationToken ct)
    {
        if (rotation == Guid.Empty) throw new FormatException("A rotation identifier is required.");
        await using var transaction = await AccountAccessLock.AcquireAsync(db, user, legacyAccount: false, ct, shared: true);
        await EnsureDeviceAsync(user, device, ct);
        var project = await LockProjectAsync(projectId, ct);
        await projects.EnsureIsOwnerAsync(user, projectId, ct);
        if (project.CurrentEpoch == uint.MaxValue) throw new ConflictException("The project cannot advance its encryption epoch.");
        var now = await DatabaseTimeAsync(ct);
        await db.ProjectEpochStages.Where(stage => stage.ProjectId == projectId).Where(ExpiredAt(now)).ExecuteDeleteAsync(ct);
        await EnsureReservationAvailableAsync(projectId, rotation, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO project_epoch_stages (id, project_id, device_id, previous_epoch, previous_server_seq, created_at, last_activity_at, count)
            VALUES ({rotation}, {projectId}, {device}, {project.CurrentEpoch}, {project.LastServerSeq}, {now}, {now}, 0)
            ON CONFLICT (id) DO NOTHING
            """, ct);
        var reserved = await LockStageAsync(rotation, device, ct);
        if (reserved.ProjectId != projectId) throw new ConflictException("The rotation identifier is already in use.");
        CheckBoundary(project, reserved.PreviousEpoch, reserved.PreviousServerSeq);
        await transaction.CommitAsync(ct);
        return new((uint)reserved.PreviousEpoch, reserved.PreviousServerSeq);
    }

    public async Task CancelAsync(Guid user, Guid device, Guid rotation, CancellationToken ct)
    {
        await using var transaction = await AccountAccessLock.AcquireAsync(db, user, legacyAccount: false, ct, shared: true);
        await EnsureDeviceAsync(user, device, ct);
        var original = await db.ProjectEpochStages.AsNoTracking().Where(stage => stage.Id == rotation)
            .Select(stage => new { stage.ProjectId, stage.DeviceId, stage.CreatedAt }).SingleOrDefaultAsync(ct);
        if (original is null) return;
        await projects.EnsureIsOwnerAsync(user, original.ProjectId, ct);
        await LockProjectAsync(original.ProjectId, ct);
        await projects.EnsureIsOwnerAsync(user, original.ProjectId, ct);
        var deleted = await db.ProjectEpochStages.Where(stage => stage.Id == rotation && stage.ProjectId == original.ProjectId &&
            stage.DeviceId == original.DeviceId && stage.CreatedAt == original.CreatedAt).ExecuteDeleteAsync(ct);
        if (deleted == 0 && await db.ProjectEpochStages.AnyAsync(stage => stage.Id == rotation, ct))
            throw new ConflictException("The project transition changed. Refresh and try again.");
        await transaction.CommitAsync(ct);
    }

    private async Task EnsureReservationAvailableAsync(Guid projectId, Guid rotation, CancellationToken ct)
    {
        if (await db.ProjectEpochStages.AnyAsync(stage => stage.ProjectId == projectId && stage.Id != rotation, ct))
            throw new ConflictException("Another project key rotation is already in progress.");
    }
}
