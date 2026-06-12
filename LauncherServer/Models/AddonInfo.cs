namespace LauncherServer.Models;

/// <summary>
/// Информация об аддоне (моде), полученная из ServerData.json
/// </summary>
public sealed class AddonInfo
{
    /// <summary>
    /// Уникальный ID мода (из ServerData.json)
    /// </summary>
    public required string ModId { get; init; }
    
    /// <summary>
    /// Название мода
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Версия мода
    /// </summary>
    public required string Version { get; init; }
    
    /// <summary>
    /// Путь к папке мода
    /// </summary>
    public required string Path { get; init; }
    
    /// <summary>
    /// Имя папки (для идентификации)
    /// </summary>
    public required string FolderName { get; init; }
    
    /// <summary>
    /// Общий размер файлов
    /// </summary>
    public long TotalSize { get; set; }
    
    /// <summary>
    /// Количество файлов
    /// </summary>
    public int FileCount { get; set; }
    
    /// <summary>
    /// Changelog из ServerData.json
    /// </summary>
    public string? Changelog { get; set; }
    
    /// <summary>
    /// Зависимости мода
    /// </summary>
    public List<AddonDependency>? Dependencies { get; set; }
}

public sealed class AddonDependency
{
    public required string ModId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
}
