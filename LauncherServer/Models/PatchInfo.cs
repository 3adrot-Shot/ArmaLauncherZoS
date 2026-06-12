using MessagePack;

namespace LauncherServer.Models;

/// <summary>
/// Represents a delta patch between two versions
/// </summary>
[MessagePackObject]
public sealed record PatchInfo
{
    /// <summary>
    /// Source version
    /// </summary>
    [Key(0)]
    public required string FromVersion { get; init; }

    /// <summary>
    /// Target version
    /// </summary>
    [Key(1)]
    public required string ToVersion { get; init; }

    /// <summary>
    /// Size of the patch file
    /// </summary>
    [Key(2)]
    public required long PatchSize { get; init; }

    /// <summary>
    /// BLAKE3 hash of the patch file
    /// </summary>
    [Key(3)]
    public required string PatchHash { get; init; }

    /// <summary>
    /// File this patch applies to
    /// </summary>
    [Key(4)]
    public required string FilePath { get; init; }

    /// <summary>
    /// Patch algorithm used
    /// </summary>
    [Key(5)]
    public PatchAlgorithm Algorithm { get; init; } = PatchAlgorithm.BsDiff4;
}

public enum PatchAlgorithm : byte
{
    BsDiff4 = 0,
    Xdelta3 = 1,
    Zstd = 2
}
