using MessagePack;

namespace LauncherServer.Models;

/// <summary>
/// Represents a single chunk of a model file
/// </summary>
[MessagePackObject]
public sealed record ChunkInfo
{
    /// <summary>
    /// BLAKE3 hash of the chunk content (hex, 64 chars)
    /// </summary>
    [Key(0)]
    public required string Hash { get; init; }

    /// <summary>
    /// Offset in the original file
    /// </summary>
    [Key(1)]
    public required long Offset { get; init; }

    /// <summary>
    /// Original uncompressed size
    /// </summary>
    [Key(2)]
    public required int Size { get; init; }

    /// <summary>
    /// Compressed size (zstd)
    /// </summary>
    [Key(3)]
    public required int CompressedSize { get; init; }

    /// <summary>
    /// Compression algorithm used
    /// </summary>
    [Key(4)]
    public CompressionType Compression { get; init; } = CompressionType.Zstd;
}

public enum CompressionType : byte
{
    None = 0,
    Zstd = 1,
    Brotli = 2
}
