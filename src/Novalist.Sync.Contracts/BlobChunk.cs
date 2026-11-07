namespace Novalist.Sync.Contracts;

public sealed record BlobChunk(int Index, byte[] Ciphertext);

public static class BlobTransfer
{
    public const int PlaintextChunkSize = 64 * 1024;
    public const int MinimumCiphertextSize = 16;
    public const int MaximumCiphertextSize = PlaintextChunkSize + MinimumCiphertextSize;
}
