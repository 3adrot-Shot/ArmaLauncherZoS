using System.IO;
using System.Security.Cryptography;

namespace ArmaLauncherClient.Services;

/// <summary>
/// Content-Defined Chunking using FastCDC algorithm.
/// Must use identical parameters as server for chunks to match!
/// </summary>
public static class FastCdcChunker
{
    // Gear table for rolling hash (random 64-bit values) - MUST MATCH SERVER
    private static readonly ulong[] GearTable = GenerateGearTable();

    // Target chunk sizes - MUST MATCH SERVER
    private const int MinChunkSize = 256 * 1024;      // 256 KB minimum
    private const int AvgChunkSize = 1024 * 1024;     // 1 MB average  
    private const int MaxChunkSize = 4 * 1024 * 1024; // 4 MB maximum
    
    // Read buffer size - larger = fewer syscalls
    private const int ReadBufferSize = 16 * 1024 * 1024; // 16 MB read buffer

    // Masks for finding chunk boundaries
    private static readonly ulong MaskS = CalculateMask(AvgChunkSize / 2);
    private static readonly ulong MaskL = CalculateMask(AvgChunkSize * 2);

    /// <summary>
    /// Chunk a file using FastCDC algorithm - returns list of (Hash, Offset, Size)
    /// Optimized for large files with 16MB read buffer
    /// </summary>
    public static async Task<List<LocalChunk>> ChunkFileAsync(string filePath, CancellationToken ct = default)
    {
        var result = new List<LocalChunk>();
        var fileInfo = new FileInfo(filePath);
        
        if (fileInfo.Length == 0) return result;

        // Use larger buffer for better I/O performance
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: ReadBufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

        // Pre-allocate based on expected chunk count
        var expectedChunks = (int)(fileInfo.Length / AvgChunkSize) + 1;
        result.Capacity = expectedChunks;

        var buffer = new byte[ReadBufferSize];
        int bufferOffset = 0;
        int bufferLen = 0;
        long fileOffset = 0;
        int chunkIndex = 0;

        while (fileOffset < fileInfo.Length)
        {
            ct.ThrowIfCancellationRequested();

            // Refill buffer if needed
            if (bufferOffset >= bufferLen - MaxChunkSize)
            {
                // Move remaining data to start of buffer
                var remaining = bufferLen - bufferOffset;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(buffer, bufferOffset, buffer, 0, remaining);
                }
                bufferLen = remaining;
                bufferOffset = 0;
                
                // Read more data
                var bytesToRead = Math.Min(buffer.Length - bufferLen, (int)Math.Min(int.MaxValue, fileInfo.Length - fileOffset - remaining));
                if (bytesToRead > 0)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(bufferLen, bytesToRead), ct);
                    bufferLen += bytesRead;
                }
            }
            
            var dataAvailable = bufferLen - bufferOffset;
            if (dataAvailable <= 0) break;
            
            var isLastPart = fileOffset + dataAvailable >= fileInfo.Length;
            var chunkSize = FindChunkBoundary(buffer.AsSpan(bufferOffset, dataAvailable), isLastPart);

            var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(bufferOffset, chunkSize))).ToLowerInvariant();

            result.Add(new LocalChunk
            {
                Index = chunkIndex++,
                Offset = fileOffset,
                Size = chunkSize,
                Hash = hash
            });

            bufferOffset += chunkSize;
            fileOffset += chunkSize;
        }

        return result;
    }

    private static int FindChunkBoundary(ReadOnlySpan<byte> data, bool isLastChunk)
    {
        int length = data.Length;
        
        if (length <= MinChunkSize)
            return length;

        int i = MinChunkSize;
        ulong hash = 0;

        int normalPoint = Math.Min(i + (AvgChunkSize - MinChunkSize) / 2, length);
        int endPoint = Math.Min(MaxChunkSize, length);

        for (; i < normalPoint; i++)
        {
            hash = (hash << 1) + GearTable[data[i]];
            if ((hash & MaskS) == 0)
                return i;
        }

        for (; i < endPoint; i++)
        {
            hash = (hash << 1) + GearTable[data[i]];
            if ((hash & MaskL) == 0)
                return i;
        }

        return endPoint;
    }

    private static ulong CalculateMask(int targetSize)
    {
        int bits = (int)Math.Ceiling(Math.Log2(targetSize));
        return (1UL << bits) - 1;
    }

    /// <summary>
    /// Generate gear table - MUST use same seed as server!
    /// </summary>
    private static ulong[] GenerateGearTable()
    {
        var table = new ulong[256];
        var random = new Random(0x1337BEEF); // Fixed seed - MUST MATCH SERVER
        
        var bytes = new byte[8];
        for (int i = 0; i < 256; i++)
        {
            random.NextBytes(bytes);
            table[i] = BitConverter.ToUInt64(bytes);
        }
        
        return table;
    }
}

public class LocalChunk
{
    public int Index { get; set; }
    public long Offset { get; set; }
    public int Size { get; set; }
    public string Hash { get; set; } = "";
}
