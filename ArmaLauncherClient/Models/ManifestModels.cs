using MessagePack;

namespace ArmaLauncherClient.Models;

public enum CompressionType : byte
{
    None = 0,
    Zstd = 1,
    Brotli = 2
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

public enum PatchAlgorithm : byte
{
    BsDiff4 = 0,
    Xdelta3 = 1,
    Zstd = 2
}

[MessagePackObject]
public sealed record ChunkInfo
{
    [Key(0)] public required string Hash { get; init; }
    [Key(1)] public required long Offset { get; init; }
    [Key(2)] public required int Size { get; init; }
    [Key(3)] public required int CompressedSize { get; init; }
    [Key(4)] public CompressionType Compression { get; init; } = CompressionType.Zstd;
}

[MessagePackObject]
public sealed record FileEntry
{
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public required long Size { get; init; }
    [Key(2)] public required string Hash { get; init; }
    [Key(3)] public required List<ChunkInfo> Chunks { get; init; }
    [Key(4)] public FileType Type { get; init; } = FileType.Binary;
}

[MessagePackObject]
public sealed record PatchInfo
{
    [Key(0)] public required string FromVersion { get; init; }
    [Key(1)] public required string ToVersion { get; init; }
    [Key(2)] public required long PatchSize { get; init; }
    [Key(3)] public required string PatchHash { get; init; }
    [Key(4)] public required string FilePath { get; init; }
    [Key(5)] public PatchAlgorithm Algorithm { get; init; } = PatchAlgorithm.BsDiff4;
}

[MessagePackObject]
public sealed record Manifest
{
    [Key(0)] public required string ModelId { get; init; }
    [Key(1)] public required string Version { get; init; }
    [Key(2)] public required DateTimeOffset CreatedAt { get; init; }
    [Key(3)] public required long TotalSize { get; init; }
    [Key(4)] public required long TotalCompressedSize { get; init; }
    [Key(5)] public required List<FileEntry> Files { get; init; }
    [Key(6)] public string? PreviousVersion { get; init; }
    [Key(7)] public List<PatchInfo>? AvailablePatches { get; init; }
    [Key(8)] public int FormatVersion { get; init; } = 1;
}

[MessagePackObject]
public sealed record SignedManifest
{
    [Key(0)] public required byte[] ManifestData { get; init; }
    [Key(1)] public required byte[] Signature { get; init; }
    [Key(2)] public required string KeyId { get; init; }
    [Key(3)] public required DateTimeOffset SignedAt { get; init; }
}
