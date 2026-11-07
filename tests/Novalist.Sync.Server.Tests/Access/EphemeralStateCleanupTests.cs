using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Tests.Infrastructure;
using Npgsql;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class EphemeralStateCleanupTests(DatabaseFixture database)
{
    [Fact]
    public async Task FailedTickIsLoggedAndTheNextTickPrunesWithoutStoppingTheWorker()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, Novalist.Sync.Server.Data.MembershipRole.Owner));
        await using (var db = database.CreateContext())
        {
            db.FieldLeaseAttempts.Add(new() { ProjectId = project, DeviceId = device, LeaseId = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UnixEpoch });
            await db.SaveChangesAsync(ct);
        }
        var failure = new FailFirstPrune();
        await using var services = new ServiceCollection().AddScoped(_ => database.CreateContext(failure))
            .AddScoped<EphemeralStateCleanup>().BuildServiceProvider();
        var clock = new ManualClock();
        var log = new FailureLog();
        using var worker = new EphemeralStateCleanupService(services.GetRequiredService<IServiceScopeFactory>(), log, clock);
        await worker.StartAsync(ct);
        try
        {
            var timer = await clock.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            timer.Tick();
            await log.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.NotNull(worker.ExecuteTask);
            Assert.False(worker.ExecuteTask.IsCompleted);
            timer.Tick();
            await failure.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await using var db = database.CreateContext();
            Assert.Empty(await db.FieldLeaseAttempts.Where(attempt => attempt.ProjectId == project).ToListAsync(ct));
            Assert.False(worker.ExecuteTask.IsCompleted);
        }
        finally { await worker.StopAsync(ct); }
        Assert.True(worker.ExecuteTask.IsCompleted);
    }

    private sealed class FailFirstPrune : DbCommandInterceptor
    {
        private int attempts;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM field_lease_attempts", StringComparison.Ordinal) && Interlocked.Increment(ref attempts) == 1)
                throw new NpgsqlException("Temporary cleanup failure: opaque-payload-sentinel.");
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM project_epoch_stages", StringComparison.Ordinal)) Completed.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailureLog : ILogger<EphemeralStateCleanupService>
    {
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            Assert.Equal(LogLevel.Warning, level);
            Assert.Contains(nameof(NpgsqlException), format(state, error));
            Assert.Null(error);
            Assert.DoesNotContain("opaque-payload-sentinel", format(state, error));
            Failed.TrySetResult();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public TaskCompletionSource<ManualTimer> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Ready.TrySetResult(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        public void Tick() { if (!disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
