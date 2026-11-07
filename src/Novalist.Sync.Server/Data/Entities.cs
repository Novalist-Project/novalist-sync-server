namespace Novalist.Sync.Server.Data;

public sealed class User
{
    public Guid Id { get; set; }
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public string? Email { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public byte[]? SigningPublicKey { get; set; }
    public byte[]? ReceivingPublicKey { get; set; }
    public Guid? LinkedByDeviceId { get; set; }
    public byte[]? LinkingSignature { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; }
    public long CurrentEpoch { get; set; }
    public DateTimeOffset EpochStartedAt { get; set; }
    public long LastServerSeq { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum MembershipRole
{
    Owner = 1,
    Contributor = 2,
    Viewer = 3
}

public sealed class Membership
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public MembershipRole Role { get; set; }
}

public sealed class StoredUpdate
{
    public Guid ProjectId { get; set; }
    public long ServerSeq { get; set; }
    public Guid ItemId { get; set; }
    public long Epoch { get; set; }
    public Guid SenderDeviceId { get; set; }
    public long SenderSeq { get; set; }
    public byte[] Header { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Signature { get; set; } = [];
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class Invitation
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? RedeemedBy { get; set; }
    public Guid? ActivatedDeviceId { get; set; }
    public byte[]? CertificateHash { get; set; }
}

public sealed class FieldLease
{
    public Guid ProjectId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid LeaseId { get; set; }
    public long Epoch { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class FieldLeaseAttempt
{
    public Guid ProjectId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid LeaseId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ProjectDeviceAuthorization
{
    public Guid ProjectId { get; set; }
    public Guid DeviceId { get; set; }
    public long Epoch { get; set; }
    public byte[] Certificate { get; set; } = [];
}

public sealed class ProjectEpochStage
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid DeviceId { get; set; }
    public long PreviousEpoch { get; set; }
    public long PreviousServerSeq { get; set; }
    public long Count { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProjectEpochEnvelope
{
    public Guid RotationId { get; set; }
    public long Index { get; set; }
    public long SenderSeq { get; set; }
    public byte[] Payload { get; set; } = [];
}

public sealed class ProjectEpochKeyGrant
{
    public Guid ProjectId { get; set; }
    public Guid DeviceId { get; set; }
    public long Epoch { get; set; }
    public long FirstServerSeq { get; set; }
    public Guid GrantorDeviceId { get; set; }
    public byte[] Payload { get; set; } = [];
}

public sealed class ProjectEpochDeviceGrant
{
    public Guid RotationId { get; set; }
    public Guid DeviceId { get; set; }
    public byte[] Certificate { get; set; } = [];
    public byte[] Payload { get; set; } = [];
}
