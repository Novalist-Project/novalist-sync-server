using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class ProjectEpochRotationTests(DatabaseFixture database)
{
    [Fact]
    public async Task RotationAfterDeviceRevocationPreservesMembershipAndExcludesTheRevokedDevice()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var member = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var memberDevice = await database.CreateDeviceAsync(member.Id);
        var stolenDevice = await database.CreateDeviceAsync(member.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (member.Id, MembershipRole.Contributor));
        await using var db = database.CreateContext();
        foreach (var id in new[] { device, memberDevice, stolenDevice })
            db.ProjectDeviceAuthorizations.Add(new() { ProjectId = project, DeviceId = id, Epoch = 0, Certificate = new byte[182] });
        await db.SaveChangesAsync(ct);
        await new DeviceRegistry(db, database.DeliveryGate).RevokeAsync(member.Id, stolenDevice, ct);
        var leases = new FieldLeaseAccess(db, new(db, database.DeliveryGate), new(db));
        await leases.AcquireAsync(member.Id, memberDevice,
            new(project, Guid.NewGuid(), 0, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1)), ct);
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var rotation = Guid.NewGuid();
        await service.ReserveAsync(owner.Id, device, project, rotation, ct);
        var checkpoint = TestData.Envelope(project, device, 1);
        checkpoint = checkpoint with { Header = (EnvelopeHeader.Parse(checkpoint.Header) with { Epoch = 1 }).ToBytes() };
        await service.StageAsync(owner.Id, device, rotation, checkpoint, ct);
        var request = JsonSerializer.Deserialize<CommitProjectEpoch>(JsonSerializer.Serialize(new
        {
            RotationId = rotation,
            Grants = new[] { device, memberDevice }.Select(id => new ProjectEpochGrant(id, new byte[182], new byte[128]))
        }))!;

        await service.CommitAsync(owner.Id, device, request, ct);

        Assert.Equal(new ProjectEpochStatus(1, 1), await service.StatusAsync(member.Id, project, ct));
        var members = await db.Memberships.Where(value => value.ProjectId == project).ToListAsync(ct);
        Assert.Equal(MembershipRole.Owner, members.Single(value => value.UserId == owner.Id).Role);
        Assert.Equal(MembershipRole.Contributor, members.Single(value => value.UserId == member.Id).Role);
        var grants = await db.ProjectEpochKeyGrants.Where(value => value.ProjectId == project).ToListAsync(ct);
        Assert.Equal(2, grants.Count);
        Assert.DoesNotContain(grants, value => value.DeviceId == stolenDevice);
        Assert.NotNull(await service.ReadGrantAsync(member.Id, memberDevice, project, 0, ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadGrantAsync(member.Id, stolenDevice, project, 0, ct));
        Assert.All(await service.DevicesAsync(owner.Id, project, ct), value => Assert.Equal(1U, value.Epoch));
        Assert.False(await db.ProjectEpochStages.AnyAsync(value => value.ProjectId == project, ct));
        Assert.False(await db.FieldLeases.AnyAsync(value => value.ProjectId == project, ct));
    }
}
