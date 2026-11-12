using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class KeyBoundEnrollmentTests(DatabaseFixture database)
{
    [Theory]
    [InlineData(null, HttpStatusCode.NoContent)]
    [InlineData("salt", HttpStatusCode.Forbidden)]
    [InlineData("keys", HttpStatusCode.Forbidden)]
    [InlineData("recovery", HttpStatusCode.Forbidden)]
    [InlineData("subject", HttpStatusCode.Forbidden)]
    [InlineData("missing-mfa", HttpStatusCode.Forbidden)]
    [InlineData("stale", HttpStatusCode.Forbidden)]
    [InlineData("challenge", HttpStatusCode.BadRequest)]
    [InlineData("short-salt", HttpStatusCode.BadRequest)]
    public async Task OneSignInRegistersOnlyTheKeysItsNonceWasDerivedFrom(string? invalid, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = Server();
        using var http = server.CreateClient();
        var subject = Guid.NewGuid().ToString();
        http.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(subject));
        var account = await http.GetFromJsonAsync<DeviceRegistrationContext>(Protocol.DeviceRegistrationPath, ct);
        Assert.NotNull(account);
        using var device = new TestRecoveryDevice();
        using var recovery = new TestRecoveryDevice();
        using var substituted = new TestRecoveryDevice();
        var salt = RandomNumberGenerator.GetBytes(DeviceProof.EnrollmentSaltLength);
        var nonce = DeviceProof.EnrollmentNonce(SyncServer.PublicUrl, device.Keys, recovery.Keys, salt);
        var token = IdentityToken(invalid == "subject" ? "another-account" : subject, nonce,
            invalid == "missing-mfa" ? "pwd" : "mfa", DateTimeOffset.UtcNow.AddMinutes(invalid == "stale" ? -6 : 0));
        var ordinary = invalid == "keys" ? substituted : device;
        var backup = invalid == "recovery" ? substituted : recovery;
        var proofSalt = invalid switch { "salt" => RandomNumberGenerator.GetBytes(32), "short-salt" => new byte[16], _ => salt };
        var request = TestEnrollment.First(account.UserId, ordinary.Keys, ordinary.Signing, backup) with
        { EnrollmentProof = new(invalid == "challenge" ? Guid.NewGuid() : Guid.Empty, token, proofSalt) };
        using var enrolled = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, request, ct);
        Assert.Equal(expected, enrolled.StatusCode);
        await using (var db = database.CreateContext())
            Assert.Equal(invalid is null ? 2 : 0, await db.Devices.CountAsync(value => value.UserId == account.UserId && value.SigningPublicKey != null, ct));
        if (invalid is not null) return;
        using var replayed = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, request, ct);
        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
        using var retry = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, request with { EnrollmentProof = null }, ct);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    [Fact]
    public async Task ARejectedProofReleasesItsNonceForACorrectedSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = Server();
        using var http = server.CreateClient();
        var subject = Guid.NewGuid().ToString();
        http.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(subject));
        var account = await http.GetFromJsonAsync<DeviceRegistrationContext>(Protocol.DeviceRegistrationPath, ct);
        Assert.NotNull(account);
        using var device = new TestRecoveryDevice();
        using var recovery = new TestRecoveryDevice();
        var salt = RandomNumberGenerator.GetBytes(DeviceProof.EnrollmentSaltLength);
        var nonce = DeviceProof.EnrollmentNonce(SyncServer.PublicUrl, device.Keys, recovery.Keys, salt);
        var request = TestEnrollment.First(account.UserId, device.Keys, device.Signing, recovery);
        using var rejected = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath,
            request with { EnrollmentProof = new(Guid.Empty, IdentityToken(subject, nonce, "pwd", DateTimeOffset.UtcNow), salt) }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using var accepted = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath,
            request with { EnrollmentProof = new(Guid.Empty, IdentityToken(subject, nonce, "mfa", DateTimeOffset.UtcNow), salt) }, ct);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    private WebApplicationFactory<Program> Server() => new SyncServer(database).WithWebHostBuilder(builder =>
        builder.UseSetting("Authentication:NativeClient:ClientId", "novalist"));

    private static string IdentityToken(string subject, string nonce, string method, DateTimeOffset authenticated) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Audience = "novalist",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["nonce"] = nonce,
                ["amr"] = new[] { method },
                ["auth_time"] = authenticated.ToUnixTimeSeconds()
            },
            SigningCredentials = new(TestTokens.SigningKey, SecurityAlgorithms.HmacSha256)
        });
}
