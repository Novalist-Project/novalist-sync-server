using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Tests.Infrastructure;
using NSec.Cryptography;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class AccountIdentityResetHistoryTests(DatabaseFixture database)
{
    [Fact]
    public async Task ResetRetainsSignedHistoryKeysAndOtherAccountsAndAbortsOldConnections()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database, settings: new Dictionary<string, string>
        { ["Authentication:NativeClient:ClientId"] = "novalist" });
        var user = await database.CreateUserAsync();
        var owner = await database.CreateUserAsync();
        var ownerDevice = await database.CreateKeyedDeviceAsync(owner.Id);
        using var signing = Key.Create(SignatureAlgorithm.Ed25519);
        var device = Guid.NewGuid();
        var recovery = await database.CreateKeyedDeviceAsync(user.Id, revoked: true);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (user.Id, MembershipRole.Contributor));
        var invitation = Guid.NewGuid();
        var header = new EnvelopeHeader(1, MsgType.Update, project, Guid.NewGuid(), 0, device, 1).ToBytes();
        var signature = SignatureAlgorithm.Ed25519.Sign(signing, header);
        await using (var db = database.CreateContext())
        {
            db.Devices.Add(new()
            {
                Id = device,
                UserId = user.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                SigningPublicKey = signing.PublicKey.Export(KeyBlobFormat.RawPublicKey),
                ReceivingPublicKey = new byte[32]
            });
            db.Updates.Add(new()
            {
                ProjectId = project,
                ServerSeq = 1,
                SenderDeviceId = device,
                SenderSeq = 1,
                ItemId = Guid.NewGuid(),
                Header = header,
                Ciphertext = [1, 2, 3],
                Nonce = [4, 5],
                Signature = signature,
                ReceivedAt = DateTimeOffset.UtcNow
            });
            db.Invitations.Add(new()
            {
                Id = invitation,
                ProjectId = project,
                RedeemedBy = user.Id,
                ActivatedDeviceId = device,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                CertificateHash = [3]
            });
            await db.SaveChangesAsync(ct);
        }
        var before = await HistoryAsync(project, owner.Id, ownerDevice, ct);
        using var http = server.CreateClient(TestTokens.Create(user.Subject));
        using var response = await http.PostAsync(Protocol.AccountResetChallengePath, null, ct);
        var challenge = (await response.Content.ReadFromJsonAsync<DeviceEnrollmentChallenge>(ct))!;
        var registry = server.Services.GetRequiredService<ConnectionRegistry>();
        var oldConnection = new FakeHubCallerContext();
        var ownerConnection = new FakeHubCallerContext();
        registry.Add(oldConnection, user.Id, device);
        registry.Add(ownerConnection, owner.Id, ownerDevice);
        var identityProof = AccountIdentityResetTests.IdentityToken(user.Subject, challenge.Nonce);
        using var reset = await http.PostAsJsonAsync(Protocol.AccountResetPath,
            new AccountIdentityResetRequest(challenge.Id, identityProof, true), ct);
        reset.EnsureSuccessStatusCode();
        var replacement = (await reset.Content.ReadFromJsonAsync<AccountIdentityResetResult>(ct))!;
        var audit = Assert.Single(server.Logs.Entries, entry => entry.Category == typeof(AccountIdentityReset).FullName);
        Assert.Contains(user.Id, audit.Values);
        Assert.Contains(replacement.UserId, audit.Values);
        Assert.DoesNotContain(server.Logs.Entries, entry => entry.Message.Contains(identityProof, StringComparison.Ordinal) ||
            entry.Values.Contains(identityProof) || entry.ScopeValues.Contains(identityProof));
        Assert.True(oldConnection.Aborted);
        Assert.False(ownerConnection.Aborted);
        Assert.Equal(before, await HistoryAsync(project, owner.Id, ownerDevice, ct));
        await using var after = database.CreateContext();
        var archivedDevice = await after.Devices.SingleAsync(value => value.Id == device, ct);
        Assert.NotNull(archivedDevice.RevokedAt);
        Assert.Equal(user.Id, archivedDevice.UserId);
        Assert.Equal(signing.PublicKey.Export(KeyBlobFormat.RawPublicKey), archivedDevice.SigningPublicKey);
        Assert.True(SignatureAlgorithm.Ed25519.Verify(signing.PublicKey,
            (await after.Updates.SingleAsync(value => value.ProjectId == project, ct)).Header, signature));
        Assert.NotNull((await after.Devices.SingleAsync(value => value.Id == recovery, ct)).RevokedAt);
    }

    [Fact]
    public async Task PersistenceFailureRollsBackArchivalAndRevocationAndAllowsRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var failure = new FailReplacement();
        await using var server = new SyncServer(database, commands: failure, settings: new Dictionary<string, string>
        { ["Authentication:NativeClient:ClientId"] = "novalist" });
        var user = await database.CreateUserAsync();
        var device = await database.CreateKeyedDeviceAsync(user.Id);
        using var http = server.CreateClient(TestTokens.Create(user.Subject));
        using var response = await http.PostAsync(Protocol.AccountResetChallengePath, null, ct);
        var challenge = (await response.Content.ReadFromJsonAsync<DeviceEnrollmentChallenge>(ct))!;
        var request = new AccountIdentityResetRequest(challenge.Id, AccountIdentityResetTests.IdentityToken(user.Subject, challenge.Nonce), true);
        var principal = TestTokens.Principal(("iss", TestTokens.Issuer), ("sub", user.Subject));
        await using (var scope = server.Services.CreateAsyncScope())
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<AccountIdentityReset>().ResetAsync(principal, request, ct));
        Assert.True(failure.Failed);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(user.Issuer, (await db.Users.SingleAsync(value => value.Id == user.Id, ct)).Issuer);
            Assert.Null((await db.Devices.SingleAsync(value => value.Id == device, ct)).RevokedAt);
            Assert.Single(await db.Users.Where(value => value.Subject == user.Subject).ToListAsync(ct));
        }
        using var retry = await http.PostAsJsonAsync(Protocol.AccountResetPath, request, ct);
        retry.EnsureSuccessStatusCode();
    }

    private async Task<string> HistoryAsync(Guid project, Guid owner, Guid device, CancellationToken ct)
    {
        await using var db = database.CreateContext();
        return JsonSerializer.Serialize(new
        {
            project = await db.Projects.SingleAsync(value => value.Id == project, ct),
            updates = await db.Updates.Where(value => value.ProjectId == project).OrderBy(value => value.ServerSeq).ToListAsync(ct),
            invitations = await db.Invitations.Where(value => value.ProjectId == project).OrderBy(value => value.Id).ToListAsync(ct),
            memberships = await db.Memberships.Where(value => value.ProjectId == project).OrderBy(value => value.UserId).ToListAsync(ct),
            owner = await db.Users.SingleAsync(value => value.Id == owner, ct),
            ownerDevice = await db.Devices.SingleAsync(value => value.Id == device, ct)
        });
    }

    private sealed class FailReplacement : DbCommandInterceptor
    {
        public bool Failed { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Failed && command.CommandText.Contains("INSERT INTO users", StringComparison.Ordinal))
            {
                Failed = true;
                throw new InvalidOperationException("Injected replacement failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
