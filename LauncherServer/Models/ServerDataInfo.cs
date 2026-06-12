using System.Text.Json.Serialization;

namespace LauncherServer.Models;

/// <summary>
/// Информация о моде из ServerData.json
/// </summary>
public class ServerDataInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("revision")]
    public RevisionInfo? Revision { get; set; }
}

public class RevisionInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
    
    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = "";
    
    [JsonPropertyName("changelog")]
    public string Changelog { get; set; } = "";
    
    [JsonPropertyName("corrupted")]
    public bool Corrupted { get; set; }
    
    [JsonPropertyName("dependencies")]
    public List<DependencyInfo>? Dependencies { get; set; }
}

public class DependencyInfo
{
    [JsonPropertyName("assetId")]
    public string AssetId { get; set; } = "";
    
    [JsonPropertyName("assetName")]
    public string AssetName { get; set; } = "";
    
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

// ==================== META FILE MODELS ====================

/// <summary>
/// Информация о моде из файла meta (без расширения)
/// </summary>
public class MetaFileWrapper
{
    [JsonPropertyName("meta")]
    public MetaInfo? Meta { get; set; }
}

public class MetaInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
    
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";
    
    [JsonPropertyName("versions")]
    public List<MetaVersionInfo>? Versions { get; set; }
    
    [JsonPropertyName("selectedRev")]
    public int SelectedRev { get; set; }
}

public class MetaVersionInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
    
    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = "";
    
    [JsonPropertyName("changelog")]
    public string Changelog { get; set; } = "";
    
    [JsonPropertyName("corrupted")]
    public bool Corrupted { get; set; }
    
    [JsonPropertyName("dependencies")]
    public List<MetaDependencyInfo>? Dependencies { get; set; }
}

public class MetaDependencyInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}
