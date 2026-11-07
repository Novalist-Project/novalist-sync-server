using System.ComponentModel.DataAnnotations;

namespace Novalist.Sync.Server.Blobs;

// Configured through the BlobStorage section, e.g. BlobStorage__UnreferencedGracePeriod=30.00:00:00.
public sealed class BlobStorageOptions
{
    public const string Section = "BlobStorage";

    [Range(typeof(long), "16", "9223372036854775807")]
    public long MaxProjectBytes { get; set; } = 1024L * 1024 * 1024;

    // Minimum time between the first reference report that omits a blob and the later report that
    // makes it collectable. Matches the update history grace period, so a checkpoint recovered within
    // that window still finds the blobs it references.
    [Range(typeof(TimeSpan), "00:00:00", "3650.00:00:00")]
    public TimeSpan UnreferencedGracePeriod { get; set; } = TimeSpan.FromDays(30);

    // Blobs deleted per cleanup batch. Each blob cascades to up to 16 384 chunks at the 1 GiB default quota.
    [Range(1, 10_000)]
    public int CollectionBatchSize { get; set; } = 100;

    // Bounds one cleanup tick; the rest is deleted on the next ticks.
    [Range(1, 1_000)]
    public int CollectionMaxBatchesPerRun { get; set; } = 20;
}
