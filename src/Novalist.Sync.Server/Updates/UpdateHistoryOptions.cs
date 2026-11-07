using System.ComponentModel.DataAnnotations;

namespace Novalist.Sync.Server.Updates;

// Configured through the UpdateHistory section, e.g. UpdateHistory__CompactionGracePeriod=30.00:00:00.
public sealed class UpdateHistoryOptions
{
    public const string Section = "UpdateHistory";

    // How long updates replaced by a rotation checkpoint are kept before deletion. Until then a broken
    // checkpoint can still be recovered from them; catch-up never serves them either way.
    [Range(typeof(TimeSpan), "00:00:00", "3650.00:00:00")]
    public TimeSpan CompactionGracePeriod { get; set; } = TimeSpan.FromDays(30);

    [Range(1, 100_000)]
    public int CompactionBatchSize { get; set; } = 5_000;

    // Bounds one cleanup tick; the rest is deleted on the next ticks.
    [Range(1, 1_000)]
    public int CompactionMaxBatchesPerRun { get; set; } = 20;
}
