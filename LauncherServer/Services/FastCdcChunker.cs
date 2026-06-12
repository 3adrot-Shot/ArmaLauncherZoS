using System.Security.Cryptography;

namespace LauncherServer.Services;

/// <summary>
/// Content-Defined Chunking using FastCDC algorithm.
/// Creates chunks with boundaries based on file content, not fixed positions.
/// This means insertions/deletions only affect nearby chunks, not all subsequent ones.
/// </summary>
public static class FastCdcChunker
{
    // Gear table for rolling hash (random 64-bit values)
    private static readonly ulong[] GearTable = GenerateGearTable();

    // Target chunk sizes (can be configured)
    private const int MinChunkSize = 256 * 1024;      // 256 KB minimum
    private const int AvgChunkSize = 1024 * 1024;     // 1 MB average  
    private const int MaxChunkSize = 4 * 1024 * 1024; // 4 MB maximum

    // Masks for finding chunk boundaries
    // More bits = larger average chunk size
    private static readonly ulong MaskS = CalculateMask(AvgChunkSize / 2); // For small chunks
    private static readonly ulong MaskL = CalculateMask(AvgChunkSize * 2); // For large chunks

    /// <summary>
    /// Chunk a file using FastCDC algorithm
    /// </summary>
    public static async Task<List<CdcChunk>> ChunkFileAsync(string filePath, CancellationToken ct = default)
    {
        var result = new List<CdcChunk>();
        var fileInfo = new FileInfo(filePath);
        
        if (fileInfo.Length == 0) return result;

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: MaxChunkSize, useAsync: true);

        var buffer = new byte[MaxChunkSize];
        long fileOffset = 0;
        int chunkIndex = 0;

        while (fileOffset < fileInfo.Length)
        {
            ct.ThrowIfCancellationRequested();

            // Read up to MaxChunkSize bytes
            var bytesRemaining = fileInfo.Length - fileOffset;
            var bytesToRead = (int)Math.Min(MaxChunkSize, bytesRemaining);
            
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);
            if (bytesRead == 0) break;

            // Find chunk boundary using FastCDC
            var chunkSize = FindChunkBoundary(buffer, bytesRead, bytesRemaining <= MaxChunkSize);

            // Compute hash for this chunk
            var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, chunkSize))).ToLowerInvariant();

            result.Add(new CdcChunk
            {
                Index = chunkIndex++,
                Offset = fileOffset,
                Size = chunkSize,
                Hash = hash
            });

            // If we didn't use all bytes, seek back
            if (chunkSize < bytesRead)
            {
                stream.Seek(fileOffset + chunkSize, SeekOrigin.Begin);
            }

            fileOffset += chunkSize;
        }

        return result;
    }

    /// <summary>
    /// Find chunk boundary using normalized FastCDC
    /// </summary>
    private static int FindChunkBoundary(ReadOnlySpan<byte> data, int length, bool isLastChunk)
    {
        if (length <= MinChunkSize)
            return length;

        if (length >= MaxChunkSize || isLastChunk && length <= MaxChunkSize)
        {
            // For last chunk or if data is max size, find boundary or use all
        }

        // Start looking for boundary after minimum size
        int i = MinChunkSize;
        ulong hash = 0;

        // Normalized chunking: use different masks for different regions
        // This helps achieve more consistent chunk sizes
        int normalPoint = Math.Min(i + (AvgChunkSize - MinChunkSize) / 2, length);
        int endPoint = Math.Min(MaxChunkSize, length);

        // Region 1: MinChunkSize to normalPoint - use stricter mask (MaskS)
        // This makes it harder to find a boundary early, pushing towards average size
        for (; i < normalPoint; i++)
        {
            hash = (hash << 1) + GearTable[data[i]];
            if ((hash & MaskS) == 0)
                return i;
        }

        // Region 2: normalPoint to MaxChunkSize - use looser mask (MaskL)  
        // This makes it easier to find a boundary, preventing chunks from getting too large
        for (; i < endPoint; i++)
        {
            hash = (hash << 1) + GearTable[data[i]];
            if ((hash & MaskL) == 0)
                return i;
        }

        // No boundary found, use maximum size
        return endPoint;
    }

    /// <summary>
    /// Calculate mask for target chunk size
    /// More 1-bits = harder to match = larger chunks
    /// </summary>
    private static ulong CalculateMask(int targetSize)
    {
        // Number of bits needed: log2(targetSize)
        int bits = (int)Math.Ceiling(Math.Log2(targetSize));
        return (1UL << bits) - 1;
    }

    /// <summary>
    /// Generate gear table with random values
    /// Uses fixed seed for reproducibility
    /// </summary>
    private static ulong[] GenerateGearTable()
    {
        var table = new ulong[256];
        var random = new Random(0x1337BEEF); // Fixed seed for reproducibility
        
        var bytes = new byte[8];
        for (int i = 0; i < 256; i++)
        {
            random.NextBytes(bytes);
            table[i] = BitConverter.ToUInt64(bytes);
        }
        
        return table;
    }
}

/// <summary>
/// A content-defined chunk
/// </summary>
public class CdcChunk
{
    public int Index { get; set; }
    public long Offset { get; set; }
    public int Size { get; set; }
    public string Hash { get; set; } = "";
}
