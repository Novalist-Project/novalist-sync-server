namespace Novalist.Sync.Contracts;

// One device in a linking chain. LinkedByDeviceId and LinkingSignature are null for an account's first device.
public sealed record ProjectDeviceLink(DevicePublicKeys Keys, Guid? LinkedByDeviceId, byte[]? LinkingSignature, bool Revoked);

// An active device of a project member that has no project authorization yet.
// Chain starts with the device itself and follows LinkedByDeviceId towards the account's first device.
public sealed record PendingProjectDevice(Guid UserId, IReadOnlyList<ProjectDeviceLink> Chain);

public sealed record AuthorizeProjectDevice(Guid ProjectId, uint Epoch, ProjectEpochGrant Grant);

public static class ProjectDeviceAuthorizationLimits
{
    // Longer chains are cut off; the last link then names a linking device that is not included.
    public const int MaximumLinkChain = 64;
}
