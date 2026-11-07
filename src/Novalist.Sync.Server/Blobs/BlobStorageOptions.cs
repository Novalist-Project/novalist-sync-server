using System.ComponentModel.DataAnnotations;

namespace Novalist.Sync.Server.Blobs;

public sealed class BlobStorageOptions
{
    public const string Section = "BlobStorage";

    [Range(typeof(long), "16", "9223372036854775807")]
    public long MaxProjectBytes { get; set; } = 1024L * 1024 * 1024;
}
