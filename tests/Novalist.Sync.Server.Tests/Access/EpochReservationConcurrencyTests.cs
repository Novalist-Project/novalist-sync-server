using Novalist.Sync.Contracts;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class EpochReservationConcurrencyTests(DatabaseFixture database)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AppendWaitsForUploadRenewalAndChecksExpiryAfterLocks(bool expiresWhileWaiting, bool grantOnly)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var start = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rotation = Guid.NewGuid();
        await using (var reserving = database.CreateContext(new DatabaseTimeOverride(start)))
        {
            await new ProjectEpochAccess(reserving, new(reserving, database.DeliveryGate))
                .ReserveAsync(owner.Id, device, project, rotation, ct);
        }

        var renewedAt = start + ProjectEpochAccess.ReservationLifetime - TimeSpan.FromSeconds(1);
        var pause = new SqlCommandPause(grantOnly ? "INSERT INTO project_epoch_device_grants" : "INSERT INTO project_epoch_envelopes");
        await using var staging = database.CreateContext(new DatabaseTimeOverride(renewedAt), pause);
        // Keep the backend stable while observing its transaction from a third connection.
        await staging.Database.OpenConnectionAsync(ct);
        var stagingPid = await staging.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
        var epochs = new ProjectEpochAccess(staging, new(staging, database.DeliveryGate));
        Task renewal = grantOnly
            ? epochs.StageGrantAsync(owner.Id, device, rotation, new(device, RandomNumberGenerator.GetBytes(182), RandomNumberGenerator.GetBytes(128)), ct)
            : epochs.StageAsync(owner.Id, device, rotation, Rotations.Update(project, device, 1, 1), ct);
        var pushClock = new DatabaseTimeOverride(start + ProjectEpochAccess.ReservationLifetime + TimeSpan.FromSeconds(1));
        await using var pushing = database.CreateContext(pushClock);
        await using var transaction = await pushing.Database.BeginTransactionAsync(ct);
        // Match field pushes: both owner operations hold compatible shared account locks.
        await pushing.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {owner.Id} FOR SHARE").ToListAsync(ct);
        var pushingPid = await pushing.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
        Task<long>? append = null;
        try
        {
            await await Task.WhenAny(pause.Entered.Task, renewal).WaitAsync(ct);
            Assert.True(pause.Entered.Task.IsCompletedSuccessfully);
            append = new UpdateLog(pushing).AppendAsync(owner.Id, TestData.Envelope(project, device, 2), ct);
            await AssertBlockedByRenewalAsync(pushingPid, stagingPid, append, ct);
            if (expiresWhileWaiting) pushClock.Now = renewedAt + ProjectEpochAccess.ReservationLifetime;
            pause.Release.TrySetResult();
            await renewal;
            if (expiresWhileWaiting)
            {
                Assert.Equal(1, await append);
                await transaction.CommitAsync(ct);
            }
            else
            {
                var error = await Assert.ThrowsAsync<PushRejectedException>(() => append);
                Assert.Equal(Protocol.TransitionInProgress, error.Message);
                await transaction.RollbackAsync(ct);
            }
            await using var checking = database.CreateContext();
            Assert.Equal(expiresWhileWaiting ? 1L : 0L,
                await checking.Projects.Where(value => value.Id == project).Select(value => value.LastServerSeq).SingleAsync(ct));
            Assert.Equal(expiresWhileWaiting ? 1 : 0, await checking.Updates.CountAsync(value => value.ProjectId == project, ct));
            Assert.Equal(renewedAt,
                (await checking.ProjectEpochStages.SingleAsync(value => value.Id == rotation, ct)).LastActivityAt);
        }
        finally
        {
            pause.Release.TrySetResult();
            await renewal.WaitAsync(ct);
            if (append is { IsCompleted: false }) await append.WaitAsync(ct);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendLocksApparentlyExpiredStageBeforeReadingDatabaseTime(bool expiresWhileWaiting)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var start = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rotation = Guid.NewGuid();
        await using (var reserving = database.CreateContext(new DatabaseTimeOverride(start)))
        {
            await new ProjectEpochAccess(reserving, new(reserving, database.DeliveryGate))
                .ReserveAsync(owner.Id, device, project, rotation, ct);
        }

        // Isolate the stage lock from the upload's project lock to catch pre-lock expiry filtering.
        await using var renewing = database.CreateContext();
        await using var renewal = await renewing.Database.BeginTransactionAsync(ct);
        var renewedAt = start + ProjectEpochAccess.ReservationLifetime - TimeSpan.FromSeconds(1);
        await renewing.ProjectEpochStages.Where(stage => stage.Id == rotation)
            .ExecuteUpdateAsync(set => set.SetProperty(stage => stage.LastActivityAt, renewedAt), ct);
        var renewingPid = await renewing.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
        var clock = new DatabaseTimeOverride(start + ProjectEpochAccess.ReservationLifetime + TimeSpan.FromSeconds(1));
        await using var pushing = database.CreateContext(clock);
        await pushing.Database.OpenConnectionAsync(ct);
        var pushingPid = await pushing.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
        var append = new UpdateLog(pushing).AppendAsync(owner.Id, TestData.Envelope(project, device, 2), ct);
        try
        {
            await AssertBlockedByRenewalAsync(pushingPid, renewingPid, append, ct);
            if (expiresWhileWaiting) clock.Now = renewedAt + ProjectEpochAccess.ReservationLifetime;
            await renewal.CommitAsync(ct);
            if (expiresWhileWaiting) Assert.Equal(1, await append);
            else await Assert.ThrowsAsync<PushRejectedException>(() => append);
            await using var checking = database.CreateContext();
            Assert.Equal(expiresWhileWaiting ? 1L : 0L,
                await checking.Projects.Where(value => value.Id == project).Select(value => value.LastServerSeq).SingleAsync(ct));
            Assert.Equal(expiresWhileWaiting ? 1 : 0, await checking.Updates.CountAsync(value => value.ProjectId == project, ct));
        }
        finally
        {
            if (renewing.Database.CurrentTransaction is not null) await renewal.RollbackAsync(ct);
            if (!append.IsCompleted) await append.WaitAsync(ct);
        }
    }

    private async Task AssertBlockedByRenewalAsync(int pushingPid, int stagingPid, Task<long> append, CancellationToken ct)
    {
        await using var observing = database.CreateContext();
        while (!append.IsCompleted)
        {
            if (await observing.Database.SqlQuery<bool>($"SELECT {stagingPid} = ANY(pg_blocking_pids({pushingPid})) AS \"Value\"").SingleAsync(ct))
                return;
            await Task.Yield();
        }
        Assert.Fail("Append completed without waiting for the stage renewal lock.");
    }
}
