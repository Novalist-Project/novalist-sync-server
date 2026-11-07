using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Npgsql;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class FieldLeaseConcurrencyTests(DatabaseFixture database)
{
    [Fact]
    public async Task IndependentProjectsOfOneAccountProceedTogetherWhileRevocationWaitsForTheirWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var otherDevice = await database.CreateDeviceAsync(user.Id);
        var first = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        var second = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        var request = new FieldLeaseRequest(first, Guid.NewGuid(), 0, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var pause = new SqlCommandPause("clock_timestamp()");
        await using var firstDb = database.CreateContext(pause);
        var holding = Service(firstDb).AcquireAsync(user.Id, device, request, ct);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await using var secondDb = database.CreateContext();
            var independent = request with { ProjectId = second };
            Assert.Equal(otherDevice, (await Service(secondDb).AcquireAsync(user.Id, otherDevice, independent, ct)
                .WaitAsync(TimeSpan.FromSeconds(5), ct)).DeviceId);
            await using var revoking = database.CreateContext();
            await revoking.Database.OpenConnectionAsync(ct);
            var process = ((NpgsqlConnection)revoking.Database.GetDbConnection()).ProcessID;
            var revoke = new DeviceRegistry(revoking, database.DeliveryGate).RevokeAsync(user.Id, device, ct);
            await DatabaseLocks.WaitUntilBlockedAsync(database, process, ct);
            pause.Release.TrySetResult();
            await holding;
            await revoke;
            await Assert.ThrowsAsync<AccessDeniedException>(() => Service(secondDb).AcquireAsync(user.Id, device, independent, ct));
        }
        finally { pause.Release.TrySetResult(); }
    }

    private FieldLeaseAccess Service(SyncDbContext db) => new(db, new(db, database.DeliveryGate), new(db));
}
