using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class FieldLeaseTimeTests : IAsyncLifetime
{
    private readonly DatabaseFixture database = new();
    public ValueTask InitializeAsync() => database.InitializeAsync();
    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task HubReturnsDatabaseTimeAndAcceptsDeadlinesDerivedFromItDespiteHostClockSkew()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var server = new SyncServer(database, commands: new DatabaseTimeOverride(now));
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await using var hub = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        await hub.StartAsync(ct);
        var remoteTime = await hub.InvokeAsync<DateTimeOffset>("GetFieldLeaseTime", ct);
        Assert.Equal(now, remoteTime);
        var request = new FieldLeaseRequest(project, Guid.NewGuid(), 0, Guid.NewGuid(), remoteTime.AddMinutes(1));
        var grant = await hub.InvokeAsync<FieldLeaseGrant>("AcquireFieldLease", request, ct);
        Assert.Equal(now, grant.ServerTime);
        Assert.Equal(device, grant.DeviceId);
        var missingDeadline = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<FieldLeaseGrant>("AcquireFieldLease", request with { AcquireBefore = null }, ct));
        Assert.Contains("acquisition deadline", missingDeadline.Message);
        var tooLate = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<FieldLeaseGrant>("AcquireFieldLease", request with { AcquireBefore = now.AddMinutes(3) }, ct));
        Assert.Contains("acquisition deadline", tooLate.Message);
        var expired = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<FieldLeaseGrant>("AcquireFieldLease", request with { AcquireBefore = now }, ct));
        Assert.Contains("focus request expired", expired.Message);
        Assert.NotNull(await hub.InvokeAsync<FieldLeaseGrant?>("RenewFieldLease", request with { AcquireBefore = null }, ct));
        Assert.True(await hub.InvokeAsync<bool>("ReleaseFieldLease", request with { AcquireBefore = null }, ct));
        Assert.False(await hub.InvokeAsync<bool>("ReleaseFieldLease", request with { AcquireBefore = null }, ct));
        await using var db = database.CreateContext();
        await db.Devices.Where(value => value.Id == device).ExecuteUpdateAsync(set => set.SetProperty(value => value.RevokedAt, now), ct);
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<DateTimeOffset>("GetFieldLeaseTime", ct));
    }
}
