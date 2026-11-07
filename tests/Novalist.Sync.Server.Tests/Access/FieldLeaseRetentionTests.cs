using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class FieldLeaseRetentionTests : IAsyncLifetime
{
    private readonly DatabaseFixture database = new();
    public ValueTask InitializeAsync() => database.InitializeAsync();
    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task OriginalExpiredRequestCannotReturnAfterCleanupOrReleaseTheNextFocus()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        var clock = new DatabaseTimeOverride(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var db = database.CreateContext(clock);
        var leases = new FieldLeaseAccess(db, new(db, database.DeliveryGate), new(db));
        var original = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), clock.Now.AddMinutes(1));
        await leases.AcquireAsync(user.Id, device, original, ct);
        Assert.True(await leases.ReleaseAsync(user.Id, device, original, ct));
        clock.Now = clock.Now.AddMinutes(2);
        await new EphemeralStateCleanup(db).PruneAsync(ct);
        Assert.Empty(await db.FieldLeaseAttempts.Where(value => value.ProjectId == project).ToListAsync(ct));
        var expired = await Assert.ThrowsAsync<ConflictException>(() => leases.AcquireAsync(user.Id, device, original, ct));
        Assert.Contains("expired", expired.Message);
        var next = original with { LeaseId = Guid.NewGuid(), AcquireBefore = clock.Now.AddMinutes(1) };
        await leases.AcquireAsync(user.Id, device, next, ct);
        Assert.False(await leases.ReleaseAsync(user.Id, device, original, ct));
        Assert.NotNull(await leases.RenewAsync(user.Id, device, next, ct));
    }

    [Fact]
    public async Task RetainedAttemptRejectsAReleasedLeaseWhileItsAcquisitionDeadlineIsStillLive()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await using var db = database.CreateContext();
        var leases = new FieldLeaseAccess(db, new(db, database.DeliveryGate), new(db));
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), (await leases.TimeAsync(ct)).AddMinutes(1));
        await leases.AcquireAsync(user.Id, device, request, ct);
        Assert.True(await leases.ReleaseAsync(user.Id, device, request, ct));
        Assert.True(request.AcquireBefore > await leases.TimeAsync(ct));
        var error = await Assert.ThrowsAsync<ConflictException>(() => leases.AcquireAsync(user.Id, device, request, ct));
        Assert.Contains("new lease identifier", error.Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("future")]
    public async Task LiveLeaseReleasesDespiteAnAcquireOnlyDeadlineOrFullAttemptQuota(string deadline)
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await using var db = database.CreateContext();
        var leases = new FieldLeaseAccess(db, new(db, database.DeliveryGate), new(db));
        var now = await leases.TimeAsync(ct);
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), now.AddMinutes(1));
        await leases.AcquireAsync(user.Id, device, request, ct);
        db.FieldLeaseAttempts.AddRange(Enumerable.Range(1, FieldLeaseAccess.MaximumRetainedAttempts - 1).Select(_ => new FieldLeaseAttempt
        {
            ProjectId = project,
            DeviceId = device,
            LeaseId = Guid.NewGuid(),
            ExpiresAt = now.AddMinutes(1)
        }));
        await db.SaveChangesAsync(ct);
        var release = request with { AcquireBefore = deadline == "missing" ? null : now.AddHours(1) };
        Assert.True(await leases.ReleaseAsync(user.Id, device, release, ct));
        Assert.False(await leases.ReleaseAsync(user.Id, device, request with { AcquireBefore = null }, ct));
        await Assert.ThrowsAsync<ConflictException>(() => leases.AcquireAsync(user.Id, device, request, ct));
    }

    [Fact]
    public async Task CleanupUsesDatabaseTimeAndRetainsAttemptsWhoseDeadlineIsStillLiveThere()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        var now = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new DatabaseTimeOverride(now);
        await using var db = database.CreateContext(clock);
        var retained = Guid.NewGuid();
        db.FieldLeaseAttempts.AddRange(
            new() { ProjectId = project, DeviceId = device, LeaseId = Guid.NewGuid(), ExpiresAt = now.AddMinutes(-1) },
            new() { ProjectId = project, DeviceId = device, LeaseId = retained, ExpiresAt = now.AddMinutes(1) });
        await db.SaveChangesAsync(ct);
        var cleanup = new EphemeralStateCleanup(db);
        await cleanup.PruneAsync(ct);
        Assert.Equal(retained, (await db.FieldLeaseAttempts.SingleAsync(value => value.ProjectId == project, ct)).LeaseId);
        clock.Now = now.AddMinutes(2);
        await cleanup.PruneAsync(ct);
        Assert.Empty(await db.FieldLeaseAttempts.Where(value => value.ProjectId == project).ToListAsync(ct));
    }

    [Fact]
    public async Task CleanupDeletesExpiredAttemptsWithoutAllowingTheirAcquisitionToReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), expired);
        await using var db = database.CreateContext();
        db.FieldLeaseAttempts.Add(new() { ProjectId = project, DeviceId = device, LeaseId = request.LeaseId, ExpiresAt = expired });
        await db.SaveChangesAsync(ct);
        await new EphemeralStateCleanup(db).PruneAsync(ct);
        Assert.Empty(await db.FieldLeaseAttempts.Where(value => value.ProjectId == project).ToListAsync(ct));
        var leases = new FieldLeaseAccess(db, new(db, database.DeliveryGate), new(db));
        await Assert.ThrowsAsync<ConflictException>(() => leases.AcquireAsync(user.Id, device, request, ct));
        await Assert.ThrowsAsync<FormatException>(() => leases.AcquireAsync(user.Id, device, request with { AcquireBefore = null }, ct));
        await Assert.ThrowsAsync<FormatException>(() => leases.AcquireAsync(user.Id, device, request with { AcquireBefore = DateTimeOffset.UtcNow.AddHours(1) }, ct));
    }

    [Fact]
    public async Task PerDeviceAttemptLimitIsBoundedAndExpiredRowsMakeRoomForNewFocus()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var otherDevice = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        await using var db = database.CreateContext();
        db.FieldLeaseAttempts.AddRange(Enumerable.Range(0, FieldLeaseAccess.MaximumRetainedAttempts).Select(_ => new FieldLeaseAttempt
        {
            ProjectId = project,
            DeviceId = device,
            LeaseId = Guid.NewGuid(),
            ExpiresAt = deadline
        }));
        await db.SaveChangesAsync(ct);
        var leases = new FieldLeaseAccess(db, new(db, database.DeliveryGate), new(db));
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), deadline);
        await Assert.ThrowsAsync<ConflictException>(() => leases.AcquireAsync(user.Id, device, request, ct));
        Assert.Equal(otherDevice, (await leases.AcquireAsync(user.Id, otherDevice, request with { ResourceId = Guid.NewGuid() }, ct)).DeviceId);
        await db.FieldLeaseAttempts.Where(value => value.ProjectId == project && value.DeviceId == device)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.ExpiresAt, DateTimeOffset.UnixEpoch), ct);
        Assert.Equal(device, (await leases.AcquireAsync(user.Id, device, request, ct)).DeviceId);
        Assert.Equal(1, await db.FieldLeaseAttempts.CountAsync(value => value.ProjectId == project && value.DeviceId == device, ct));
    }
}
