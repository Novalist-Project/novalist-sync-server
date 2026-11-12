using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Novalist.Sync.Contracts;

public sealed record DeviceRegistrationContext(Guid UserId, string ServerUrl);
public sealed record DevicePublicKeys(Guid DeviceId, byte[] SigningPublicKey, byte[] ReceivingPublicKey);
public sealed record DeviceRecoveryRegistration(DevicePublicKeys Keys, byte[] Signature, byte[] LinkingSignature);
public sealed record VerifiedDeviceRegistration(DevicePublicKeys Keys, byte[] Signature, Guid? LinkedByDeviceId = null,
    byte[]? LinkingSignature = null, DeviceRecoveryRegistration? RecoveryDevice = null, DeviceEnrollmentProof? EnrollmentProof = null);
public sealed record DeviceConnectionChallenge(Guid UserId, Guid DeviceId, Guid Nonce, string ServerUrl);

public static class DeviceProof
{
    public const int EnrollmentSaltLength = 32;

    public static byte[] EncodeRegistration(string serverUrl, Guid userId, DevicePublicKeys keys)
    {
        if (userId == Guid.Empty) throw new FormatException("Invalid device registration identities or public keys.");
        return [.. "novalist/v1/device-register"u8, .. EncodeServerUrl(serverUrl), .. userId.ToByteArray(bigEndian: true), .. EncodeKeys(keys)];
    }

    // The OIDC nonce of a key-bound first-device sign-in. The account is unknown before sign-in, so the
    // nonce binds the server and both public-key records; the ID token's subject binds the account.
    public static string EnrollmentNonce(string serverUrl, DevicePublicKeys keys, DevicePublicKeys recoveryKeys, byte[] salt)
    {
        if (salt is not { Length: EnrollmentSaltLength }) throw new FormatException("An enrollment salt must contain 32 bytes.");
        var hash = SHA256.HashData([.. "novalist/v1/device-enrollment"u8, .. EncodeServerUrl(serverUrl), .. EncodeKeys(keys),
            .. EncodeKeys(recoveryKeys), .. salt]);
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] EncodeConnection(DeviceConnectionChallenge challenge)
    {
        if (challenge.UserId == Guid.Empty || challenge.DeviceId == Guid.Empty || challenge.Nonce == Guid.Empty)
            throw new FormatException("Invalid device connection challenge.");
        return [.. "novalist/v1/device-connect"u8, .. EncodeServerUrl(challenge.ServerUrl), .. challenge.UserId.ToByteArray(bigEndian: true),
            .. challenge.DeviceId.ToByteArray(bigEndian: true), .. challenge.Nonce.ToByteArray(bigEndian: true)];
    }

    public static byte[] EncodeLink(string serverUrl, Guid userId, Guid linkingDeviceId, DevicePublicKeys keys)
    {
        if (linkingDeviceId == Guid.Empty) throw new FormatException("A linking device identity is required.");
        var registration = EncodeRegistration(serverUrl, userId, keys);
        return [.. "novalist/v1/device-link"u8, .. linkingDeviceId.ToByteArray(bigEndian: true), .. registration];
    }

    public static string CanonicalServerUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsoluteUri.Length > 2048)
            throw new FormatException("Device proofs require a canonical HTTPS server URL, or loopback HTTP for tests.");
        return uri.AbsoluteUri.TrimEnd('/') + '/';
    }

    private static byte[] EncodeKeys(DevicePublicKeys keys)
    {
        if (keys is null || keys.DeviceId == Guid.Empty || keys.SigningPublicKey is not { Length: 32 } || keys.ReceivingPublicKey is not { Length: 32 })
            throw new FormatException("Invalid device registration identities or public keys.");
        return [.. keys.DeviceId.ToByteArray(bigEndian: true), .. keys.SigningPublicKey, .. keys.ReceivingPublicKey];
    }

    private static byte[] EncodeServerUrl(string value)
    {
        var url = Encoding.UTF8.GetBytes(CanonicalServerUrl(value));
        var encoded = new byte[2 + url.Length];
        BinaryPrimitives.WriteUInt16BigEndian(encoded, checked((ushort)url.Length));
        url.CopyTo(encoded, 2);
        return encoded;
    }
}
