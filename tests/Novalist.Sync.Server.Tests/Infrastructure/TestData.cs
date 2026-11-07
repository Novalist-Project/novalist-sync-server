using System.Security.Cryptography;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Tests.Infrastructure;

public static class TestData
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A user as UserDirectory would create it on first login with TestTokens.Create(subject).
    public static async Task<User> CreateUserAsync(this DatabaseFixture database, string? subject = null)
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Issuer = TestTokens.Issuer,
            Subject = subject ?? Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var db = database.CreateContext();
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    public static async Task<Guid> CreateDeviceAsync(this DatabaseFixture database, Guid userId, bool revoked = false)
    {
        var now = DateTimeOffset.UtcNow;
        var device = new Device { Id = Guid.NewGuid(), UserId = userId, CreatedAt = now, RevokedAt = revoked ? now : null };

        await using var db = database.CreateContext();
        db.Devices.Add(device);
        await db.SaveChangesAsync(Ct);
        return device.Id;
    }

    // A verified device with random public keys, optionally linked by another device of the same account.
    public static async Task<Guid> CreateKeyedDeviceAsync(this DatabaseFixture database, Guid userId, Guid? linkedBy = null, bool revoked = false)
    {
        var now = DateTimeOffset.UtcNow;
        var device = new Device
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            RevokedAt = revoked ? now : null,
            SigningPublicKey = RandomNumberGenerator.GetBytes(32),
            ReceivingPublicKey = RandomNumberGenerator.GetBytes(32),
            LinkedByDeviceId = linkedBy,
            LinkingSignature = linkedBy is null ? null : RandomNumberGenerator.GetBytes(64),
        };

        await using var db = database.CreateContext();
        db.Devices.Add(device);
        await db.SaveChangesAsync(Ct);
        return device.Id;
    }

    public static async Task<Guid> CreateProjectAsync(
        this DatabaseFixture database,
        params (Guid UserId, MembershipRole Role)[] members)
    {
        var now = DateTimeOffset.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), EpochStartedAt = now, CreatedAt = now };

        await using var db = database.CreateContext();
        db.Projects.Add(project);
        db.Memberships.AddRange(members.Select(m => new Membership { ProjectId = project.Id, UserId = m.UserId, Role = m.Role }));
        await db.SaveChangesAsync(Ct);
        return project.Id;
    }

    // Random bytes as "ciphertext": the server must behave the same for any payload.
    public static Envelope Envelope(Guid projectId, Guid deviceId, ulong senderSeq, MsgType msgType = MsgType.Update) =>
        new(
            new EnvelopeHeader(Protocol.Version, msgType, projectId, Guid.NewGuid(), Epoch: 0, deviceId, senderSeq).ToBytes(),
            RandomNumberGenerator.GetBytes(24),
            RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(32, 512)),
            RandomNumberGenerator.GetBytes(64));
}
