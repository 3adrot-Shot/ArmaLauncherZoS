using MessagePack;

namespace LauncherServer.Models;

/// <summary>
/// Represents a file within a model (e.g., model.gguf, tokenizer.json)
/// </summary>
[MessagePackObject]
public sealed record FileEntry
{
    /// <summary>
    /// Relative path within the model directory
    /// </summary>
    [Key(0)]
    public required string Path { get; init; }

    /// <summary>
    /// Total file size
    /// </summary>
    [Key(1)]
    public required long Size { get; init; }

    /// <summary>
    /// BLAKE3 hash of the complete file
    /// </summary>
    [Key(2)]
    public required string Hash { get; init; }

    /// <summary>
    /// Ordered list of chunks that make up this file
    /// </summary>
    [Key(3)]
    public required List<ChunkInfo> Chunks { get; init; }

    /// <summary>
    /// File type hint
    /// </summary>
    [Key(4)]
    public FileType Type { get; init; } = FileType.Binary;
}

public enum FileType : byte
{
    Binary = 0,
    Gguf = 1,
    SafeTensors = 2,
    Pth = 3,
    Json = 4,
    Text = 5
}
