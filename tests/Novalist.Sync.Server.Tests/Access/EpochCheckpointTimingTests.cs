using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class EpochCheckpointTimingTests(DatabaseFixture database)
{
    [Fact]
    public async Task HistoryBoundaryUsesDatabaseTimeAfterEveryCheckpointEnvelopeIsCopied()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var startedAt = new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new DatabaseTimeOverride(startedAt);
        var copies = new AdvanceClockAfterCopy(clock);
        await using var db = database.CreateContext(clock, copies);
        await Rotations.AuthorizeAsync(db, project, device, ct);
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var rotation = Guid.NewGuid();
        await epochs.ReserveAsync(owner.Id, device, project, rotation, ct);
        for (ulong seq = 1; seq <= 2; seq++)
            await epochs.StageAsync(owner.Id, device, rotation, Rotations.Update(project, device, seq, 1), ct);

        await epochs.CommitAsync(owner.Id, device, new(rotation, null, [new(device, new byte[182], new byte[128])]), ct);

        Assert.Equal(2, copies.Count);
        var boundary = await db.ProjectHistoryBoundaries.AsNoTracking().SingleAsync(value => value.ProjectId == project, ct);
        Assert.Equal(startedAt.AddHours(2), boundary.CreatedAt);
        Assert.Equal(2, await db.Updates.CountAsync(value => value.ProjectId == project, ct));
    }

    private sealed class AdvanceClockAfterCopy(DatabaseTimeOverride clock) : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO updates (", StringComparison.Ordinal))
            {
                Count++;
                clock.Now = clock.Now.AddHours(1);
            }
            return ValueTask.FromResult(result);
        }
    }
}
