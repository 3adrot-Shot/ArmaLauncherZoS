using MessagePack;

namespace LauncherServer.Models;

/// <summary>
/// Информация о чанке файла
/// </summary>
[MessagePackObject]
public class FileChunkInfo
{
    [Key(0)]
    public int Index { get; set; }
    
    [Key(1)]
    public long Offset { get; set; }
    
    [Key(2)]
    public int Size { get; set; }
    
    [Key(3)]
    public string Hash { get; set; } = "";
}
