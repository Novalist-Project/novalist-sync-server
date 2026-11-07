using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Hubs;

public sealed partial class SyncHub
{
    public async Task<ProjectEpochStatus> GetProjectEpoch(Guid projectId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.StatusAsync(UserId, projectId, Context.ConnectionAborted);
    }

    public async Task RegisterProjectDevice(Guid projectId, uint epoch, byte[] certificate)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        await epochs.RegisterAsync(UserId, DeviceId, projectId, epoch, certificate, Context.ConnectionAborted);
    }

    public async Task<IReadOnlyList<ProjectDevice>> ListProjectDevices(Guid projectId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.DevicesAsync(UserId, projectId, Context.ConnectionAborted);
    }

    public async Task BeginProjectEpoch(BeginProjectEpoch request)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        await epochs.BeginAsync(UserId, DeviceId, request, Context.ConnectionAborted);
    }

    public async Task<ProjectEpochStatus> ReserveProjectEpoch(Guid projectId, Guid rotationId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.ReserveAsync(UserId, DeviceId, projectId, rotationId, Context.ConnectionAborted);
    }

    public async Task CancelProjectEpoch(Guid rotationId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        await epochs.CancelAsync(UserId, DeviceId, rotationId, Context.ConnectionAborted);
    }

    public async Task<long> StageProjectEpoch(Guid rotationId, Envelope envelope)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.StageAsync(UserId, DeviceId, rotationId, envelope, Context.ConnectionAborted);
    }

    public async Task CommitProjectEpoch(CommitProjectEpoch request)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        var project = await epochs.CommitAsync(UserId, DeviceId, request, Context.ConnectionAborted,
            request.RemovedMemberId is { } memberId
                ? projectId => revocation.NotifyRemovedAsync(projectId, memberId, CancellationToken.None)
                : null);
        var state = await epochs.StatusAsync(UserId, project, Context.ConnectionAborted);
        await Clients.Group(ProjectGroup(project)).ProjectEpochChanged(project, state.Epoch);
    }

    public async Task StageProjectEpochGrant(Guid rotationId, ProjectEpochGrant grant)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        await epochs.StageGrantAsync(UserId, DeviceId, rotationId, grant, Context.ConnectionAborted);
    }

    public async Task<StoredProjectEpochGrant?> GetProjectEpochGrant(Guid projectId, uint afterEpoch)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.ReadGrantAsync(UserId, DeviceId, projectId, afterEpoch, Context.ConnectionAborted);
    }

    public async Task<StoredProjectEpochGrant?> GetLatestProjectEpochGrant(Guid projectId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.ReadGrantAsync(UserId, DeviceId, projectId, -1, Context.ConnectionAborted);
    }

    public async Task<IReadOnlyList<PendingProjectDevice>> ListPendingProjectDevices(Guid projectId)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        return await epochs.PendingDevicesAsync(UserId, projectId, Context.ConnectionAborted);
    }

    public async Task AuthorizeProjectDevice(AuthorizeProjectDevice request)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await RequireVerifiedAccessAsync();
        await epochs.AuthorizeDeviceAsync(UserId, DeviceId, request, Context.ConnectionAborted);
        await Clients.Clients(connections.ConnectionIdsOfDevice(request.Grant.DeviceId)).ProjectDeviceAuthorized(request.ProjectId);
    }
}
