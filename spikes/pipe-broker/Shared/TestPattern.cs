namespace Spike.Shared;

/// <summary>Deterministic 64 KiB chunk, so the probe can check a streamed download byte-for-byte without a second copy.</summary>
internal static class TestPattern
{
    public const int ChunkSize = 64 * 1024;

    public static byte[] Chunk()
    {
        var chunk = new byte[ChunkSize];
        for (var i = 0; i < chunk.Length; i++)
            chunk[i] = (byte)(i % 251);
        return chunk;
    }
}
