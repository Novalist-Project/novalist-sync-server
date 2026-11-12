namespace Novalist.Sync.Contracts;

// This deliberately forfeits project access; it does not recover keys or link a device.
public sealed record AccountIdentityResetRequest(Guid ChallengeId, string IdentityToken, bool ForfeitProjectAccess);
public sealed record AccountIdentityResetResult(Guid UserId);
