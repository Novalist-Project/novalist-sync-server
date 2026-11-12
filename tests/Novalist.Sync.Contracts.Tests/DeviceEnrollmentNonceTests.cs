using System.Security.Cryptography;
using System.Text;

namespace Novalist.Sync.Contracts.Tests;

public sealed class DeviceEnrollmentNonceTests
{
    private static readonly DevicePublicKeys Device = new(Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
        Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"),
        Convert.FromHexString("202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F"));
    private static readonly DevicePublicKeys Recovery = new(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
        Convert.FromHexString("404142434445464748494A4B4C4D4E4F505152535455565758595A5B5C5D5E5F"),
        Convert.FromHexString("606162636465666768696A6B6C6D6E6F707172737475767778797A7B7C7D7E7F"));

    [Fact]
    public void NonceHashesTheServerBothKeyRecordsAndTheSaltAsUnpaddedBase64Url()
    {
        var salt = Enumerable.Range(0, 32).Select(value => (byte)(0x80 + value)).ToArray();
        byte[] frame = [.. Encoding.UTF8.GetBytes("novalist/v1/device-enrollment"), 0, 21, .. Encoding.UTF8.GetBytes("https://sync.example/"),
            .. Convert.FromHexString("102132435465768798A9BACBDCEDFE0F"), .. Device.SigningPublicKey, .. Device.ReceivingPublicKey,
            .. Convert.FromHexString("FFEEDDCCBBAA99887766554433221100"), .. Recovery.SigningPublicKey, .. Recovery.ReceivingPublicKey, .. salt];
        var expected = Convert.ToBase64String(SHA256.HashData(frame)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, DeviceProof.EnrollmentNonce("https://sync.example", Device, Recovery, salt));
        Assert.Equal(43, expected.Length);
        Assert.NotEqual(expected, DeviceProof.EnrollmentNonce("https://sync.example/", Recovery, Device, salt));
    }

    [Fact]
    public void NonceRejectsMalformedSaltsAndKeys()
    {
        Assert.Throws<FormatException>(() => DeviceProof.EnrollmentNonce("https://sync.example/", Device, Recovery, new byte[31]));
        Assert.Throws<FormatException>(() => DeviceProof.EnrollmentNonce("https://sync.example/", Device, Recovery, null!));
        Assert.Throws<FormatException>(() => DeviceProof.EnrollmentNonce("https://sync.example/", null!, Recovery, new byte[32]));
        Assert.Throws<FormatException>(() => DeviceProof.EnrollmentNonce("https://sync.example/", Device, Recovery with { DeviceId = Guid.Empty }, new byte[32]));
        Assert.Throws<FormatException>(() => DeviceProof.EnrollmentNonce("http://sync.example/", Device, Recovery, new byte[32]));
    }
}
