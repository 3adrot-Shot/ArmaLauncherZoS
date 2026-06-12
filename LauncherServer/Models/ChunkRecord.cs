namespace LauncherServer.Models;

/// <summary>
/// Chunk record in the deduplication database
/// </summary>
public sealed record ChunkRecord
{
    /// <summary>
    /// BLAKE3 hash (primary key)
    /// </summary>
    public required string Hash { get; init; }

    /// <summary>
    /// Original uncompressed size
    /// </summary>
    public required int Size { get; init; }

    /// <summary>
    /// Compressed size
    /// </summary>
    public required int CompressedSize { get; init; }

    /// <summary>
    /// Compression type
    /// </summary>
    public CompressionType Compression { get; init; }

    /// <summary>
    /// Storage location (S3 key)
    /// </summary>
    public required string StorageKey { get; init; }

    /// <summary>
    /// Reference count for garbage collection
    /// </summary>
    public int RefCount { get; set; }

    /// <summary>
    /// First seen timestamp
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Last access timestamp
    /// </summary>
    public DateTimeOffset LastAccessedAt { get; set; }
}
