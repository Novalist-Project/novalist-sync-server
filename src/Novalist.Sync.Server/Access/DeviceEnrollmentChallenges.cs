using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public sealed class DeviceEnrollmentChallenges(TimeProvider clock)
{
    // A key-bound sign-in stays usable while its auth_time is within five minutes (plus 30 seconds of skew).
    private static readonly TimeSpan KeyBoundLifetime = TimeSpan.FromMinutes(6);
    private readonly TimeProvider clock = clock;
    private sealed record Pending(Guid User, byte[] KeysHash, DeviceEnrollmentChallenge Challenge, bool KeyBound = false)
    {
        public bool Claimed { get; set; }
    }
    private readonly object gate = new();
    private readonly Dictionary<Guid, Pending> pending = new();

    public DeviceEnrollmentChallenge Create(Guid user, string server, DeviceEnrollmentRequest request)
    {
        var hash = Hash(server, user, request.Keys, request.RecoveryKeys);
        lock (gate)
        {
            Reserve(user);
            var challenge = new DeviceEnrollmentChallenge(Guid.NewGuid(), Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
                clock.GetUtcNow().AddMinutes(5));
            pending.Add(challenge.Id, new(user, hash, challenge));
            return challenge;
        }
    }

    public EnrollmentClaim Claim(Guid user, string server, VerifiedDeviceRegistration request)
    {
        var proof = request.EnrollmentProof ?? throw new AccessDeniedException("An enrollment proof is required.");
        var recovery = request.RecoveryDevice ?? throw new AccessDeniedException("A recovery device is required.");
        if (proof.Salt is not null) return ClaimKeyBound(user, server, request.Keys, recovery.Keys, proof);
        var hash = Hash(server, user, request.Keys, recovery.Keys);
        lock (gate)
        {
            if (!pending.TryGetValue(proof.ChallengeId, out var value) || value.KeyBound || value.User != user ||
                value.Claimed || value.Challenge.ExpiresAt <= clock.GetUtcNow() || !CryptographicOperations.FixedTimeEquals(hash, value.KeysHash))
                throw new AccessDeniedException("The enrollment challenge expired or does not match these device keys.");
            value.Claimed = true;
            return new EnrollmentClaim(this, value.Challenge);
        }
    }

    // The nonce is derived from the submitted keys, so a proof only verifies for the keys it was signed in for.
    // Each nonce is accepted once: the entry stays as a tombstone after success until the sign-in is too old.
    private EnrollmentClaim ClaimKeyBound(Guid user, string server, DevicePublicKeys keys, DevicePublicKeys recovery, DeviceEnrollmentProof proof)
    {
        if (proof.ChallengeId != Guid.Empty) throw new FormatException("A key-bound enrollment proof has no challenge identifier.");
        var nonce = DeviceProof.EnrollmentNonce(server, keys, recovery, proof.Salt!);
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)).AsSpan(0, 16));
        lock (gate)
        {
            Purge();
            if (pending.ContainsKey(id)) throw new AccessDeniedException("This enrollment sign-in has already been used.");
            Reserve(user);
            var challenge = new DeviceEnrollmentChallenge(id, nonce, clock.GetUtcNow().Add(KeyBoundLifetime));
            pending.Add(id, new(user, [], challenge, KeyBound: true) { Claimed = true });
            return new EnrollmentClaim(this, challenge);
        }
    }

    private void Reserve(Guid user)
    {
        Purge();
        if (pending.Count >= 1024 || pending.Values.Count(value => value.User == user) >= 8)
            throw new ConflictException("Too many pending enrollments. Wait for an earlier challenge to expire.");
    }

    private void Purge()
    {
        foreach (var id in pending.Where(pair => pair.Value.Challenge.ExpiresAt <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray())
            pending.Remove(id);
    }

    public sealed class EnrollmentClaim(DeviceEnrollmentChallenges owner, DeviceEnrollmentChallenge challenge) : IDisposable
    {
        private bool finished;
        public DeviceEnrollmentChallenge Challenge => challenge;

        public void Validate()
        {
            lock (owner.gate)
                if (finished || !owner.pending.ContainsKey(challenge.Id) || challenge.ExpiresAt <= owner.clock.GetUtcNow())
                    throw new AccessDeniedException("The enrollment challenge expired.");
        }

        public void Complete()
        {
            lock (owner.gate)
            {
                if (owner.pending.TryGetValue(challenge.Id, out var value) && !value.KeyBound) owner.pending.Remove(challenge.Id);
                finished = true;
            }
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (!finished && owner.pending.TryGetValue(challenge.Id, out var value))
                {
                    if (value.KeyBound) owner.pending.Remove(challenge.Id);
                    else value.Claimed = false;
                }
                finished = true;
            }
        }
    }

    private static byte[] Hash(string server, Guid user, DevicePublicKeys keys, DevicePublicKeys recovery)
    {
        if (keys is null || recovery is null) throw new FormatException("Ordinary and recovery device keys are required.");
        return SHA256.HashData([.. DeviceProof.EncodeRegistration(server, user, keys), .. DeviceProof.EncodeRegistration(server, user, recovery)]);
    }
}
