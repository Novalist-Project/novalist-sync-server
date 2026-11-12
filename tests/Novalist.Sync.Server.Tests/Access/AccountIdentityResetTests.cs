using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class AccountIdentityResetTests(DatabaseFixture database)
{
    private static readonly Dictionary<string, string> Settings = new() { ["Authentication:NativeClient:ClientId"] = "novalist" };

    [Theory]
    [InlineData("valid", HttpStatusCode.OK)]
    [InlineData("no-confirmation", HttpStatusCode.BadRequest)]
    [InlineData("nonce", HttpStatusCode.Forbidden)]
    [InlineData("subject", HttpStatusCode.Forbidden)]
    [InlineData("issuer", HttpStatusCode.Forbidden)]
    [InlineData("audience", HttpStatusCode.Forbidden)]
    [InlineData("signature", HttpStatusCode.Forbidden)]
    [InlineData("missing-mfa", HttpStatusCode.Forbidden)]
    [InlineData("missing-time", HttpStatusCode.Forbidden)]
    [InlineData("stale", HttpStatusCode.Forbidden)]
    [InlineData("expired-token", HttpStatusCode.Forbidden)]
    [InlineData("other-account", HttpStatusCode.Forbidden)]
    public async Task OnlyExplicitFreshMfaForThisResetAndAccountCanReplaceTheIdentity(string scenario, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database, settings: Settings);
        var user = await database.CreateUserAsync();
        var device = await database.CreateKeyedDeviceAsync(user.Id);
        using var http = server.CreateClient(TestTokens.Create(user.Subject));
        using var response = await http.PostAsync(Protocol.AccountResetChallengePath, null, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var challenge = (await response.Content.ReadFromJsonAsync<DeviceEnrollmentChallenge>(ct))!;
        if (scenario == "other-account") http.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(Guid.NewGuid().ToString()));
        var request = new AccountIdentityResetRequest(challenge.Id, IdentityToken(user.Subject, challenge.Nonce, scenario), scenario != "no-confirmation");
        using var reset = await http.PostAsJsonAsync(Protocol.AccountResetPath, request, ct);
        Assert.Equal(expected, reset.StatusCode);
        await using var db = database.CreateContext();
        var old = await db.Users.SingleAsync(value => value.Id == user.Id, ct);
        var storedDevice = await db.Devices.SingleAsync(value => value.Id == device, ct);
        if (expected != HttpStatusCode.OK)
        {
            Assert.Equal(user.Issuer, old.Issuer);
            Assert.Null(storedDevice.RevokedAt);
            return;
        }
        var result = (await reset.Content.ReadFromJsonAsync<AccountIdentityResetResult>(ct))!;
        Assert.NotEqual(user.Id, result.UserId);
        Assert.StartsWith("urn:novalist:archived-account:", old.Issuer);
        Assert.NotNull(storedDevice.RevokedAt);
        Assert.False(await db.Devices.AnyAsync(value => value.UserId == result.UserId, ct));
        Assert.False(await db.Memberships.AnyAsync(value => value.UserId == result.UserId, ct));
        var discovered = (await http.GetFromJsonAsync<DeviceRegistrationContext>(Protocol.DeviceRegistrationPath, ct))!;
        Assert.Equal(result.UserId, discovered.UserId);
        using var replay = await http.PostAsJsonAsync(Protocol.AccountResetPath, request, ct);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(result.UserId, (await http.GetFromJsonAsync<DeviceRegistrationContext>(Protocol.DeviceRegistrationPath, ct))!.UserId);

        // Ordinary enrollment still requires its own fresh proof, and can bootstrap the replacement.
        using var ordinary = new TestRecoveryDevice();
        using var recovery = new TestRecoveryDevice();
        var registration = TestEnrollment.First(result.UserId, ordinary.Keys, ordinary.Signing, recovery);
        using var refused = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, registration, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        TestEnrollment.Authorize(http, ordinary.Keys.SigningPublicKey);
        using var enrolled = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, registration, ct);
        Assert.Equal(HttpStatusCode.NoContent, enrolled.StatusCode);
    }

    [Fact]
    public async Task ResetAndEnrollmentProofsCannotBeSubstitutedForEachOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database, settings: Settings);
        var user = await database.CreateUserAsync();
        using var http = server.CreateClient(TestTokens.Create(user.Subject));
        using var ordinary = new TestRecoveryDevice();
        using var recovery = new TestRecoveryDevice();
        using var enrollmentResponse = await http.PostAsJsonAsync(Protocol.DeviceEnrollmentPath, new DeviceEnrollmentRequest(ordinary.Keys, recovery.Keys), ct);
        var enrollment = (await enrollmentResponse.Content.ReadFromJsonAsync<DeviceEnrollmentChallenge>(ct))!;
        using var resetResponse = await http.PostAsync(Protocol.AccountResetChallengePath, null, ct);
        var reset = (await resetResponse.Content.ReadFromJsonAsync<DeviceEnrollmentChallenge>(ct))!;
        using var rejectedReset = await http.PostAsJsonAsync(Protocol.AccountResetPath,
            new AccountIdentityResetRequest(enrollment.Id, IdentityToken(user.Subject, enrollment.Nonce), true), ct);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedReset.StatusCode);
        var registration = TestEnrollment.First(user.Id, ordinary.Keys, ordinary.Signing, recovery) with
        { EnrollmentProof = new(reset.Id, IdentityToken(user.Subject, reset.Nonce)) };
        using var rejectedEnrollment = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, registration, ct);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedEnrollment.StatusCode);
    }

    [Fact]
    public async Task ConcurrentResetsCannotReplaceTheSameIdentityTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database, settings: Settings);
        var user = await database.CreateUserAsync();
        using var http = server.CreateClient(TestTokens.Create(user.Subject));
        var requests = new List<AccountIdentityResetRequest>();
        for (var index = 0; index < 2; index++)
        {
            using var response = await http.PostAsync(Protocol.AccountResetChallengePath, null, ct);
            var challenge = (await response.Content.ReadFromJsonAsync<DeviceEnrollmentChallenge>(ct))!;
            requests.Add(new(challenge.Id, IdentityToken(user.Subject, challenge.Nonce), true));
        }
        var responses = await Task.WhenAll(requests.Select(request => http.PostAsJsonAsync(Protocol.AccountResetPath, request, ct)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.Users.CountAsync(value => value.Subject == user.Subject, ct));
        Assert.Single(await db.Users.Where(value => value.Subject == user.Subject && value.Issuer == TestTokens.Issuer).ToListAsync(ct));
    }

    [Fact]
    public async Task AnonymousRequestsCannotCreateOrCompleteResets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database, settings: Settings);
        using var http = server.CreateClient(null);
        using var start = await http.PostAsync(Protocol.AccountResetChallengePath, null, ct);
        using var finish = await http.PostAsJsonAsync(Protocol.AccountResetPath, new AccountIdentityResetRequest(Guid.NewGuid(), "token", true), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, start.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, finish.StatusCode);
    }

    internal static string IdentityToken(string subject, string nonce, string scenario = "valid")
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = scenario == "subject" ? "other" : subject,
            ["nonce"] = scenario == "nonce" ? "other" : nonce,
            ["amr"] = new[] { scenario == "missing-mfa" ? "pwd" : "mfa" },
            ["auth_time"] = DateTimeOffset.UtcNow.AddMinutes(scenario == "stale" ? -6 : 0).ToUnixTimeSeconds()
        };
        if (scenario == "missing-time") claims.Remove("auth_time");
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = scenario == "issuer" ? "https://other.invalid" : TestTokens.Issuer,
            Audience = scenario == "audience" ? TestTokens.Audience : "novalist",
            Expires = DateTime.UtcNow.AddMinutes(scenario == "expired-token" ? -2 : 5),
            IssuedAt = DateTime.UtcNow.AddMinutes(-3),
            NotBefore = DateTime.UtcNow.AddMinutes(-3),
            Claims = claims,
            SigningCredentials = new(scenario == "signature" ? new SymmetricSecurityKey(new byte[32]) : TestTokens.SigningKey,
                SecurityAlgorithms.HmacSha256)
        });
    }
}
