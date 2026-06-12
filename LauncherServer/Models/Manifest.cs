using MessagePack;

namespace LauncherServer.Models;

/// <summary>
/// Complete manifest for a model version
/// </summary>
[MessagePackObject]
public sealed record Manifest
{
    /// <summary>
    /// Model identifier (e.g., "llama-3.1-405b-q4_k_m")
    /// </summary>
    [Key(0)]
    public required string ModelId { get; init; }

    /// <summary>
    /// Semantic version
    /// </summary>
    [Key(1)]
    public required string Version { get; init; }

    /// <summary>
    /// When this version was created
    /// </summary>
    [Key(2)]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Total size of all files uncompressed
    /// </summary>
    [Key(3)]
    public required long TotalSize { get; init; }

    /// <summary>
    /// Total size of all compressed chunks
    /// </summary>
    [Key(4)]
    public required long TotalCompressedSize { get; init; }

    /// <summary>
    /// All files in this model
    /// </summary>
    [Key(5)]
    public required List<FileEntry> Files { get; init; }

    /// <summary>
    /// Previous version for delta patching (null if first version)
    /// </summary>
    [Key(6)]
    public string? PreviousVersion { get; init; }

    /// <summary>
    /// Available delta patches from other versions
    /// </summary>
    [Key(7)]
    public List<PatchInfo>? AvailablePatches { get; init; }

    /// <summary>
    /// Manifest format version for future compatibility
    /// </summary>
    [Key(8)]
    public int FormatVersion { get; init; } = 1;
}
