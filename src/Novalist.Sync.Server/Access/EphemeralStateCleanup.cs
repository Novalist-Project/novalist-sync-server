using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Access;

public sealed class EphemeralStateCleanup(SyncDbContext db)
{
    public async Task PruneAsync(CancellationToken ct)
    {
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        await db.FieldLeaseAttempts.Where(attempt => attempt.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        await db.ProjectEpochStages.Where(stage => stage.CreatedAt <= now - ProjectEpochAccess.ReservationLifetime).ExecuteDeleteAsync(ct);
    }
}

public sealed class EphemeralStateCleanupService(IServiceScopeFactory scopes, ILogger<EphemeralStateCleanupService> logger, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EphemeralStateCleanup>().PruneAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Ephemeral state cleanup failed ({ErrorType}); it will retry on the next tick.", error.GetType().Name);
            }
        }
    }
}
