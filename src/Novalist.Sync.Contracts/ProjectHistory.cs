namespace Novalist.Sync.Contracts;

// BytesSinceBoundary counts every stored envelope from HistoryStartsAt on, including the checkpoint
// that starts it. CheckpointBytes and LastCompactedAt are null until the first compaction or rotation.
public sealed record ProjectHistoryStatus(long HistoryStartsAt, long LastServerSeq, long BytesSinceBoundary,
    long? CheckpointBytes, DateTimeOffset? LastCompactedAt);
