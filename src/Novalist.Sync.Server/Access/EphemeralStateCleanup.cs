using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Access;

public sealed class EphemeralStateCleanup(SyncDbContext db, IOptions<UpdateHistoryOptions>? history = null)
{
    private readonly UpdateHistoryOptions options = history?.Value ?? new();

    // Returns how many superseded updates were deleted.
    public async Task<int> PruneAsync(CancellationToken ct)
    {
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        await db.FieldLeaseAttempts.Where(attempt => attempt.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        await db.ProjectEpochStages.Where(ProjectEpochAccess.ExpiredAt(now)).ExecuteDeleteAsync(ct);
        return await CompactHistoryAsync(now - options.CompactionGracePeriod, ct);
    }

    // Deletes updates before the newest history boundary that is older than the grace period. Each
    // rotation keeps its own timestamp, so frequent rotations don't keep postponing the deletion.
    private async Task<int> CompactHistoryAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var total = 0;
        for (var batch = 0; batch < options.CompactionMaxBatchesPerRun; batch++)
        {
            // Match project deletion's lock order before removing updates and checking receipt foreign keys.
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH due AS (
                    SELECT project_id, max(first_server_seq) AS first_server_seq
                    FROM project_history_boundaries
                    WHERE created_at <= {cutoff}
                    GROUP BY project_id
                ), locked_projects AS MATERIALIZED (
                    SELECT p.id AS project_id, d.first_server_seq
                    FROM projects p JOIN due d ON d.project_id = p.id
                    WHERE EXISTS (
                        SELECT 1 FROM updates u WHERE u.project_id = p.id AND u.server_seq < d.first_server_seq
                    )
                    ORDER BY p.id
                    LIMIT {options.CompactionBatchSize}
                    FOR KEY SHARE OF p
                ), superseded AS (
                    SELECT u.project_id, u.server_seq
                    FROM updates u JOIN locked_projects d ON d.project_id = u.project_id
                    WHERE u.server_seq < d.first_server_seq
                    ORDER BY u.project_id, u.server_seq
                    LIMIT {options.CompactionBatchSize}
                ), removed AS (
                    DELETE FROM updates u USING superseded s
                    WHERE u.project_id = s.project_id AND u.server_seq = s.server_seq
                    RETURNING u.*
                )
                INSERT INTO update_receipts (project_id, sender_device_id, sender_seq, server_seq,
                    header_hash, nonce_hash, ciphertext_hash, signature_hash)
                SELECT project_id, sender_device_id, sender_seq, server_seq,
                    sha256(header), sha256(nonce), sha256(ciphertext), sha256(signature)
                FROM removed
                """, ct);
            total += deleted;
            if (deleted < options.CompactionBatchSize) break;
        }
        return total;
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
                var compacted = await scope.ServiceProvider.GetRequiredService<EphemeralStateCleanup>().PruneAsync(stoppingToken);
                if (compacted > 0) logger.LogInformation("Deleted {Count} updates superseded by rotation checkpoints.", compacted);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Ephemeral state cleanup failed ({ErrorType}); it will retry on the next tick.", error.GetType().Name);
            }
        }
    }
}
