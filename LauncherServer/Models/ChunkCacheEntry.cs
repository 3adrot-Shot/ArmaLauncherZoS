using MessagePack;

namespace LauncherServer.Models;

/// <summary>
/// Запись кэша чанков с информацией об исходном файле
/// </summary>
[MessagePackObject]
public sealed class ChunkCacheEntry
{
    /// <summary>
    /// Полный путь к исходному файлу
    /// </summary>
    [Key(0)]
    public required string SourcePath { get; init; }
    
    /// <summary>
    /// Размер исходного файла
    /// </summary>
    [Key(1)]
    public required long SourceSize { get; init; }
    
    /// <summary>
    /// Время последнего изменения исходного файла (UTC ticks)
    /// </summary>
    [Key(2)]
    public required long SourceModifiedUtc { get; init; }
    
    /// <summary>
    /// Список чанков
    /// </summary>
    [Key(3)]
    public required List<FileChunkInfo> Chunks { get; init; }
}
