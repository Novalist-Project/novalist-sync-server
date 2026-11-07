namespace Novalist.Sync.Contracts;

public interface ISyncHub
{
    Task<ProjectEpochStatus> GetProjectEpoch(Guid projectId);
    Task RegisterProjectDevice(Guid projectId, uint epoch, byte[] certificate);
    Task<IReadOnlyList<ProjectDevice>> ListProjectDevices(Guid projectId);
    Task BeginProjectEpoch(BeginProjectEpoch request);
    Task<ProjectEpochStatus> ReserveProjectEpoch(Guid projectId, Guid rotationId);
    Task CancelProjectEpoch(Guid rotationId);
    Task<long> StageProjectEpoch(Guid rotationId, Envelope envelope);
    Task StageProjectEpochGrant(Guid rotationId, ProjectEpochGrant grant);
    Task CommitProjectEpoch(CommitProjectEpoch request);
    Task<StoredProjectEpochGrant?> GetProjectEpochGrant(Guid projectId, uint afterEpoch);
    Task<IReadOnlyList<ProjectMember>> ListProjectMembers(Guid projectId);
    Task RemoveProjectMember(Guid projectId, Guid memberId);
    Task DeleteProject(Guid projectId);
    Task<DateTimeOffset> GetFieldLeaseTime();
    Task<FieldLeaseGrant> AcquireFieldLease(FieldLeaseRequest request);
    Task<FieldLeaseGrant?> RenewFieldLease(FieldLeaseRequest request);
    Task<bool> ReleaseFieldLease(FieldLeaseRequest request);
    Task<long> PushFieldEdit(FieldLeaseRequest request, Envelope envelope);
    Task<long> PushGuardedFieldEdit(FieldLeaseRequest request, FieldLeaseRequest required, Envelope envelope);
    Task<DeviceConnectionChallenge> GetDeviceChallenge();
    Task AuthenticateDevice(byte[] signature);
    Task CreateInvitation(Guid projectId, CreateInvitation request);
    Task<InvitationGrant> RedeemInvitation(Guid invitationId);
    Task<InvitationActivation> ActivateInvitation(ActivateInvitation request);
    Task RevokeInvitation(Guid projectId, Guid invitationId);

    Task JoinProject(Guid projectId);
    Task LeaveProject(Guid projectId);

    Task<long> Push(Envelope envelope);
    Task PublishPresence(Envelope envelope);

    Task CreateProject(Guid projectId);

    IAsyncEnumerable<SequencedEnvelope> CatchUp(
        Guid projectId,
        long afterServerSeq,
        CancellationToken cancellationToken);
}
