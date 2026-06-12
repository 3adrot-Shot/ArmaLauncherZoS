namespace ArmaLauncherClient.Models;

public enum DownloadState
{
    Idle,
    Checking,
    Downloading,
    Assembling,
    Verifying,
    Completed,
    Failed,
    Paused
}

public enum ChunkState
{
    Pending,
    Downloading,
    Completed,
    Failed
}

public sealed class DownloadProgress
{
    public required string ModelId { get; init; }
    public required string Version { get; init; }
    public DownloadState State { get; init; }
    
    /// <summary>
    /// Total bytes downloaded so far
    /// </summary>
    public long DownloadedBytes { get; init; }
    
    /// <summary>
    /// Total bytes to download
    /// </summary>
    public long TotalBytes { get; init; }
    
    /// <summary>
    /// Current download speed in bytes per second
    /// </summary>
    public double BytesPerSecond { get; init; }
    
    /// <summary>
    /// Current file being downloaded
    /// </summary>
    public string? CurrentFile { get; init; }
    
    /// <summary>
    /// Current file index (1-based)
    /// </summary>
    public int CurrentFileIndex { get; init; }
    
    /// <summary>
    /// Total number of files
    /// </summary>
    public int TotalFiles { get; init; }
    
    // Legacy chunk-based fields (for compatibility with ChunkDownloader)
    public long CachedBytes { get; init; }
    public int TotalChunks { get; init; }
    public int DownloadedChunks { get; init; }
    public int CachedChunks { get; init; }
    public int FailedChunks { get; init; }

    /// <summary>
    /// Percent complete (0-100)
    /// </summary>
    public double PercentComplete => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100 : 0;

    /// <summary>
    /// Remaining bytes to download
    /// </summary>
    public long RemainingBytes => TotalBytes - DownloadedBytes;

    /// <summary>
    /// Formatted downloaded size (e.g. "15.5 MB")
    /// </summary>
    public string DownloadedFormatted => FormatBytes(DownloadedBytes);

    /// <summary>
    /// Formatted total size (e.g. "86.4 MB")
    /// </summary>
    public string TotalFormatted => FormatBytes(TotalBytes);

    /// <summary>
    /// Formatted remaining size (e.g. "70.9 MB")
    /// </summary>
    public string RemainingFormatted => FormatBytes(RemainingBytes);

    /// <summary>
    /// Formatted speed (e.g. "5.2 MB/s")
    /// </summary>
    public string SpeedFormatted => BytesPerSecond > 0 ? $"{FormatBytes((long)BytesPerSecond)}/s" : "";

    /// <summary>
    /// Estimated time remaining
    /// </summary>
    public string EtaFormatted
    {
        get
        {
            if (BytesPerSecond <= 0 || DownloadedBytes >= TotalBytes)
                return "";
            
            var remaining = TotalBytes - DownloadedBytes;
            var seconds = remaining / BytesPerSecond;
            
            if (seconds < 60)
                return $"{seconds:F0}s";
            if (seconds < 3600)
                return $"{seconds / 60:F0}m {seconds % 60:F0}s";
            
            return $"{seconds / 3600:F0}h {(seconds % 3600) / 60:F0}m";
        }
    }
    
    /// <summary>
    /// File progress text (e.g. "File 2/4")
    /// </summary>
    public string FileProgressText => TotalFiles > 0 ? $"File {CurrentFileIndex}/{TotalFiles}" : "";
    
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F2} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}

public sealed class ChunkDownloadStatus
{
    public required string Hash { get; init; }
    public ChunkState State { get; init; }
    public string? Error { get; init; }
}
