namespace Novalist.Sync.Contracts;

/// <param name="AcquireBefore">Fixed server-clock deadline, required for acquisition and cancellation before acquisition;
/// optional for renewal, edits, and release of an existing lease.</param>
public sealed record FieldLeaseRequest(Guid ProjectId, Guid ResourceId, uint Epoch, Guid LeaseId, DateTimeOffset? AcquireBefore = null);
public sealed record FieldLeaseGrant(Guid DeviceId, Guid LeaseId, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime, long ServerSeq);
