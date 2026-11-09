using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Hubs;

public sealed partial class SyncHub
{
    public async Task<ProjectEpochStatus> ReserveProjectCompaction(Guid projectId, Guid compactionId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.ReserveCompactionAsync(UserId, DeviceId, projectId, compactionId, Context.ConnectionAborted);
    }

    public async Task<long> StageProjectCompaction(Guid compactionId, Envelope envelope)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.StageAsync(UserId, DeviceId, compactionId, envelope, Context.ConnectionAborted, compaction: true);
    }

    public async Task CommitProjectCompaction(Guid compactionId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        var (projectId, historyStartsAt) = await epochs.CommitCompactionAsync(UserId, DeviceId, compactionId, Context.ConnectionAborted);
        await Clients.Group(ProjectGroup(projectId)).ProjectHistoryCompacted(projectId, historyStartsAt);
    }

    public async Task CancelProjectCompaction(Guid compactionId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        await epochs.CancelAsync(UserId, DeviceId, compactionId, Context.ConnectionAborted, compaction: true);
    }

    public async Task<ProjectHistoryStatus> GetProjectHistory(Guid projectId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.HistoryAsync(UserId, projectId, Context.ConnectionAborted);
    }
}
