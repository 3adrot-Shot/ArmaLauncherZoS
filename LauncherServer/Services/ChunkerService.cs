using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using ZstdSharp;
using LauncherServer.Models;

namespace LauncherServer.Services;

/// <summary>
/// Content-defined chunking with Rabin fingerprinting for optimal deduplication
/// </summary>
public sealed class ChunkerService
{
    private const ulong RabinPrime = 0xbfe6b8a5bf378d83UL;
    private const int WindowSize = 48;

    private const int MinChunkSize = 16 * 1024 * 1024;
    private const int TargetChunkSize = 32 * 1024 * 1024;
    private const int MaxChunkSize = 64 * 1024 * 1024;

    private const ulong ChunkMask = 0x0001FFFFFFFFFFFFUL;

    private readonly int _compressionLevel;
    private readonly ILogger<ChunkerService>? _logger;

    public ChunkerService(int compressionLevel = 3, ILogger<ChunkerService>? logger = null)
    {
        _compressionLevel = compressionLevel;
        _logger = logger;
    }

    public async IAsyncEnumerable<ChunkResult> ChunkFileAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("File not found", filePath);

        _logger?.LogInformation("Chunking file: {Path} ({Size:N0} bytes)", filePath, fileInfo.Length);

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        long offset = 0;
        long remaining = fileInfo.Length;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            var chunkSize = FindChunkBoundary(mmf, offset, remaining);
            var chunk = await ReadAndCompressChunkAsync(mmf, offset, chunkSize, ct);

            yield return chunk;

            offset += chunkSize;
            remaining -= chunkSize;
        }
    }

    private int FindChunkBoundary(MemoryMappedFile mmf, long offset, long remaining)
    {
        var maxSize = (int)Math.Min(MaxChunkSize, remaining);

        if (maxSize <= MinChunkSize)
            return maxSize;

        using var accessor = mmf.CreateViewAccessor(offset, maxSize, MemoryMappedFileAccess.Read);

        ulong fingerprint = 0;
        var position = MinChunkSize;

        for (int i = position - WindowSize; i < position && i >= 0; i++)
        {
            byte b = accessor.ReadByte(i);
            fingerprint = RollingHash(fingerprint, b);
        }

        while (position < maxSize)
        {
            byte b = accessor.ReadByte(position);
            fingerprint = RollingHash(fingerprint, b);

            if ((fingerprint & ChunkMask) == 0)
            {
                return position + 1;
            }

            position++;

            if (position >= TargetChunkSize && (fingerprint & (ChunkMask >> 2)) == 0)
            {
                return position + 1;
            }
        }

        return maxSize;
    }

    private static ulong RollingHash(ulong hash, byte newByte)
    {
        return (hash * RabinPrime) ^ newByte;
    }

    private async Task<ChunkResult> ReadAndCompressChunkAsync(
        MemoryMappedFile mmf,
        long offset,
        int size,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            using var accessor = mmf.CreateViewStream(offset, size, MemoryMappedFileAccess.Read);
            await accessor.ReadExactlyAsync(buffer.AsMemory(0, size), ct);

            var dataSpan = buffer.AsSpan(0, size);

            var hash = CryptoService.HashSha256(dataSpan);

            using var compressor = new Compressor(_compressionLevel);
            var compressedBound = Compressor.GetCompressBound(size);
            var compressedBuffer = ArrayPool<byte>.Shared.Rent(compressedBound);

            try
            {
                var compressedSize = compressor.Wrap(dataSpan, compressedBuffer);
                var compressedData = compressedBuffer.AsSpan(0, compressedSize).ToArray();

                return new ChunkResult
                {
                    Hash = hash,
                    Offset = offset,
                    OriginalSize = size,
                    CompressedSize = compressedSize,
                    CompressedData = compressedData,
                    Compression = CompressionType.Zstd
                };
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressedBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public sealed record ChunkResult
{
    public required string Hash { get; init; }
    public required long Offset { get; init; }
    public required int OriginalSize { get; init; }
    public required int CompressedSize { get; init; }
    public required byte[] CompressedData { get; init; }
    public CompressionType Compression { get; init; }
}
