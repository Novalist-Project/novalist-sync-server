using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class ProjectDeviceAuthorizationTests(DatabaseFixture database)
{
    [Fact]
    public async Task PendingDevicesListOnlyUnauthorizedActiveMemberDevicesWithTheirLinkChain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, _, member, project) = await CreateProjectAsync();
        var root = await database.CreateKeyedDeviceAsync(member.Id, revoked: true);
        var laptop = await database.CreateKeyedDeviceAsync(member.Id, linkedBy: root);
        var phone = await database.CreateKeyedDeviceAsync(member.Id, linkedBy: laptop);
        await database.CreateKeyedDeviceAsync(member.Id, linkedBy: laptop, revoked: true);
        var outsider = await database.CreateUserAsync();
        await database.CreateKeyedDeviceAsync(outsider.Id);
        await using var db = database.CreateContext();
        await Rotations.AuthorizeAsync(db, project, phone, ct);
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));

        var pending = Assert.Single(await service.PendingDevicesAsync(owner.Id, project, ct));

        Assert.Equal(member.Id, pending.UserId);
        Assert.Equal([laptop, root], pending.Chain.Select(link => link.Keys.DeviceId));
        Assert.Equal(root, pending.Chain[0].LinkedByDeviceId);
        Assert.NotNull(pending.Chain[0].LinkingSignature);
        Assert.Equal([false, true], pending.Chain.Select(link => link.Revoked));
        Assert.Null(pending.Chain[1].LinkedByDeviceId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.PendingDevicesAsync(member.Id, project, ct));
    }

    [Fact]
    public async Task AuthorizationStoresTheCertificateAndCurrentEpochGrantAndRotationMustIncludeTheDevice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, ownerDevice, member, project) = await CreateProjectAsync();
        var laptop = await database.CreateKeyedDeviceAsync(member.Id);
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        await new Updates.UpdateLog(db).AppendAsync(owner.Id, TestData.Envelope(project, ownerDevice, 1), ct);
        await Rotations.RotateAsync(service, owner.Id, ownerDevice, project, 2, ct);
        var request = new AuthorizeProjectDevice(project, 1, Grant(laptop));

        await service.AuthorizeDeviceAsync(owner.Id, ownerDevice, request, ct);
        await service.AuthorizeDeviceAsync(owner.Id, ownerDevice, request, ct);

        var stored = await service.ReadGrantAsync(member.Id, laptop, project, -1, ct);
        Assert.NotNull(stored);
        Assert.Equal((1U, 2L, ownerDevice), (stored.Epoch, stored.FirstServerSeq, stored.GrantorDeviceId));
        Assert.Equal(request.Grant.Payload, stored.Payload);
        Assert.Contains(await service.DevicesAsync(owner.Id, project, ct),
            value => value.DeviceId == laptop && value.Epoch == 1 && value.Certificate.SequenceEqual(request.Grant.Certificate));
        Assert.Empty(await service.PendingDevicesAsync(owner.Id, project, ct));
        await Assert.ThrowsAsync<ConflictException>(() => service.AuthorizeDeviceAsync(owner.Id, ownerDevice,
            request with { Grant = request.Grant with { Payload = RandomNumberGenerator.GetBytes(128) } }, ct));
        await Assert.ThrowsAsync<ConflictException>(() => Rotations.RotateAsync(service, owner.Id, ownerDevice, project, 3, ct));
    }

    [Fact]
    public async Task EpochZeroGrantIsReadableAsTheLatestGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, ownerDevice, member, project) = await CreateProjectAsync();
        var laptop = await database.CreateKeyedDeviceAsync(member.Id);
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));

        await service.AuthorizeDeviceAsync(owner.Id, ownerDevice, new(project, 0, Grant(laptop)), ct);

        Assert.Null(await service.ReadGrantAsync(member.Id, laptop, project, 0, ct));
        var stored = await service.ReadGrantAsync(member.Id, laptop, project, -1, ct);
        Assert.Equal((0U, 1L), (stored!.Epoch, stored.FirstServerSeq));
    }

    [Theory]
    [InlineData("non-owner", typeof(AccessDeniedException))]
    [InlineData("stale epoch", typeof(ConflictException))]
    [InlineData("unregistered caller", typeof(AccessDeniedException))]
    [InlineData("revoked caller", typeof(AccessDeniedException))]
    [InlineData("outsider device", typeof(AccessDeniedException))]
    [InlineData("revoked device", typeof(AccessDeniedException))]
    [InlineData("device without keys", typeof(AccessDeniedException))]
    [InlineData("removed member", typeof(AccessDeniedException))]
    [InlineData("missing grant", typeof(FormatException))]
    public async Task InvalidAuthorizationsAreRejectedWithoutStoringAnything(string scenario, Type expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, ownerDevice, member, project) = await CreateProjectAsync();
        var target = scenario switch
        {
            "outsider device" => await database.CreateKeyedDeviceAsync((await database.CreateUserAsync()).Id),
            "revoked device" => await database.CreateKeyedDeviceAsync(member.Id, revoked: true),
            "device without keys" => await database.CreateDeviceAsync(member.Id),
            _ => await database.CreateKeyedDeviceAsync(member.Id)
        };
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var caller = (owner.Id, ownerDevice);
        var request = new AuthorizeProjectDevice(project, 0, Grant(target));
        switch (scenario)
        {
            case "non-owner":
                var memberDevice = await database.CreateKeyedDeviceAsync(member.Id);
                await Rotations.AuthorizeAsync(db, project, memberDevice, ct);
                caller = (member.Id, memberDevice);
                break;
            case "stale epoch": request = request with { Epoch = 1 }; break;
            case "unregistered caller": caller = (owner.Id, await database.CreateDeviceAsync(owner.Id)); break;
            case "revoked caller": await new DeviceRegistry(db, database.DeliveryGate).RevokeAsync(owner.Id, ownerDevice, ct); break;
            case "removed member": await new ProjectAccess(db, database.DeliveryGate).RemoveMemberAsync(owner.Id, project, member.Id, ct); break;
            case "missing grant": request = request with { Grant = null! }; break;
        }
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync(expected, () => service.AuthorizeDeviceAsync(caller.Item1, caller.Item2, request, ct));

        Assert.False(await db.ProjectDeviceAuthorizations.AnyAsync(value => value.ProjectId == project && value.DeviceId == target, ct));
        Assert.False(await db.ProjectEpochKeyGrants.AnyAsync(value => value.ProjectId == project && value.DeviceId == target, ct));
    }

    [Fact]
    public async Task AuthorizationIsRejectedWhileARotationIsReservedAndAllowedAfterItIsCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, ownerDevice, member, project) = await CreateProjectAsync();
        var laptop = await database.CreateKeyedDeviceAsync(member.Id);
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var rotation = Guid.NewGuid();
        await service.ReserveAsync(owner.Id, ownerDevice, project, rotation, ct);
        var request = new AuthorizeProjectDevice(project, 0, Grant(laptop));

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.AuthorizeDeviceAsync(owner.Id, ownerDevice, request, ct));
        Assert.Contains("rotation is in progress", error.Message);
        await service.CancelAsync(owner.Id, ownerDevice, rotation, ct);
        db.ChangeTracker.Clear();
        await service.AuthorizeDeviceAsync(owner.Id, ownerDevice, request, ct);

        Assert.True(await db.ProjectDeviceAuthorizations.AnyAsync(value => value.ProjectId == project && value.DeviceId == laptop, ct));
    }

    [Fact]
    public async Task AuthorizedCollaboratorsCanOnlyBeRemovedByRotation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, ownerDevice, member, project) = await CreateProjectAsync();
        var laptop = await database.CreateKeyedDeviceAsync(member.Id);
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        await service.AuthorizeDeviceAsync(owner.Id, ownerDevice, new(project, 0, Grant(laptop)), ct);

        await Assert.ThrowsAsync<ConflictException>(() => new ProjectAccess(db, database.DeliveryGate).RemoveMemberAsync(owner.Id, project, member.Id, ct));
    }

    [Fact]
    public async Task AuthorizationLogContainsIdsButNoCertificateOrGrantBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, ownerDevice, member, project) = await CreateProjectAsync();
        var laptop = await database.CreateKeyedDeviceAsync(member.Id);
        await using var db = database.CreateContext();
        var captured = new CapturedLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(captured));
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate), factory.CreateLogger<ProjectEpochAccess>());
        var grant = Grant(laptop);

        await service.AuthorizeDeviceAsync(owner.Id, ownerDevice, new(project, 0, grant), ct);

        var entry = Assert.Single(captured.Entries, value => value.Message.Contains("authorized device", StringComparison.Ordinal));
        Assert.Contains(laptop.ToString(), entry.Message, StringComparison.Ordinal);
        var secrets = new[] { grant.Certificate, grant.Payload }
            .SelectMany(bytes => new[] { Convert.ToBase64String(bytes), Convert.ToHexString(bytes), Convert.ToHexStringLower(bytes) });
        foreach (var capturedEntry in captured.Entries)
            Assert.DoesNotContain(capturedEntry.Values.Concat(capturedEntry.ScopeValues), value => value is byte[] or ProjectEpochGrant);
        foreach (var logged in captured.Entries.SelectMany(value => new[] { value.Message, value.ScopeText }
            .Concat(value.Values.Select(item => item?.ToString())).Concat(value.ScopeValues.Select(item => item?.ToString()))))
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, logged ?? "", StringComparison.Ordinal));
    }

    private static ProjectEpochGrant Grant(Guid device) =>
        new(device, RandomNumberGenerator.GetBytes(182), RandomNumberGenerator.GetBytes(128));

    private async Task<(User Owner, Guid OwnerDevice, User Member, Guid Project)> CreateProjectAsync()
    {
        var owner = await database.CreateUserAsync();
        var member = await database.CreateUserAsync();
        var ownerDevice = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (member.Id, MembershipRole.Contributor));
        await using var db = database.CreateContext();
        await Rotations.AuthorizeAsync(db, project, ownerDevice, TestContext.Current.CancellationToken);
        return (owner, ownerDevice, member, project);
    }
}
