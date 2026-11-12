namespace Novalist.Sync.Contracts;

public sealed record DeviceEnrollmentRequest(DevicePublicKeys Keys, DevicePublicKeys RecoveryKeys);
public sealed record DeviceEnrollmentChallenge(Guid Id, string Nonce, DateTimeOffset ExpiresAt);

// A server challenge proof names ChallengeId. A key-bound proof leaves it empty and supplies the salt
// the client hashed into the sign-in nonce with DeviceProof.EnrollmentNonce.
public sealed record DeviceEnrollmentProof(Guid ChallengeId, string IdentityToken, byte[]? Salt = null);
