using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Hubs;

public sealed partial class SyncHub
{
    public async Task<DateTimeOffset> GetFieldLeaseTime()
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        return await fieldLeases.TimeAsync(Context.ConnectionAborted);
    }

    public async Task<FieldLeaseGrant> AcquireFieldLease(FieldLeaseRequest request)
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        return await fieldLeases.AcquireAsync(UserId, DeviceId, request, Context.ConnectionAborted);
    }

    public async Task<FieldLeaseGrant?> RenewFieldLease(FieldLeaseRequest request)
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        return await fieldLeases.RenewAsync(UserId, DeviceId, request, Context.ConnectionAborted);
    }

    public async Task<bool> ReleaseFieldLease(FieldLeaseRequest request)
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        return await fieldLeases.ReleaseAsync(UserId, DeviceId, request, Context.ConnectionAborted);
    }

    public async Task<long> PushFieldEdit(FieldLeaseRequest request, Envelope envelope)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await using var delivery = await projects.PauseDeliveryAsync(request.ProjectId, Context.ConnectionAborted);
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        var sequence = await fieldLeases.PushAsync(UserId, DeviceId, request, envelope, Context.ConnectionAborted);
        await Clients.OthersInGroup(ProjectGroup(request.ProjectId)).ReceiveUpdate(new SequencedEnvelope(sequence, envelope));
        return sequence;
    }

    public async Task<long> PushGuardedFieldEdit(FieldLeaseRequest request, FieldLeaseRequest required, Envelope envelope)
    {
        await using var access = await devices.PauseAccessChangesAsync(UserId, Context.ConnectionAborted);
        await using var delivery = await projects.PauseDeliveryAsync(request.ProjectId, Context.ConnectionAborted);
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        var sequence = await fieldLeases.PushGuardedAsync(UserId, DeviceId, request, required, envelope, Context.ConnectionAborted);
        await Clients.OthersInGroup(ProjectGroup(request.ProjectId)).ReceiveUpdate(new SequencedEnvelope(sequence, envelope));
        return sequence;
    }
}
