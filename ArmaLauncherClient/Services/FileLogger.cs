using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ArmaLauncherClient.Services;

/// <summary>
/// Simple file logger for debugging
/// </summary>
public static class FileLogger
{
    private static readonly string LogPath;
    private static readonly object Lock = new();
    private static readonly ConcurrentQueue<string> Queue = new();
    private static bool _initialized;

    // Regex patterns for IP addresses and URLs
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IpRegex = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b",
        RegexOptions.Compiled);
    
    /// <summary>
    /// Event fired when a new log entry is written (for console window)
    /// </summary>
    public static event Action<string>? OnLogEntry;
    
    /// <summary>
    /// Gets the full path to the log file
    /// </summary>
    public static string LogFilePath => LogPath;

    public static string GetCurrentLogContents()
    {
        try
        {
            lock (Lock)
            {
                return File.Exists(LogPath)
                    ? File.ReadAllText(LogPath)
                    : "Log file not found.";
            }
        }
        catch (Exception ex)
        {
            return $"Failed to read log file: {ex.Message}";
        }
    }

    static FileLogger()
    {
        LogPath = Path.Combine(AppContext.BaseDirectory, "launcher.log");
    }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        
        try
        {
            // Clear old log
            File.WriteAllText(LogPath, $"=== ArmaLauncher Log Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            Log("Logger initialized");
            Log($"Log file: {LogPath}");
        }
        catch
        {
            // Ignore
        }
    }

    public static void Log(string message)
    {
        var sanitized = SanitizeForLog(message);
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {sanitized}";
        
        try
        {
            lock (Lock)
            {
                File.AppendAllText(LogPath, line + "\n");
            }
        }
        catch
        {
            // Ignore write errors
        }
        
        // Notify subscribers (console window)
        try
        {
            OnLogEntry?.Invoke(line);
        }
        catch
        {
            // Ignore subscriber errors
        }
        
        System.Diagnostics.Debug.WriteLine(line);
    }

    public static void Log(string format, params object[] args)
    {
        Log(string.Format(format, args));
    }

    public static void Error(string message, Exception? ex = null)
    {
        Log($"ERROR: {message}");
        if (ex != null)
        {
            Log($"  Exception: {ex.GetType().Name}: {ex.Message}");
            Log($"  StackTrace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Log($"  Inner: {ex.InnerException.Message}");
            }
        }
    }

    /// <summary>
    /// Replaces IP addresses and URLs in the message with base64-encoded equivalents
    /// </summary>
    private static string SanitizeForLog(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        // First replace URLs (which may contain IPs), then standalone IPs
        var result = UrlRegex.Replace(message, match =>
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(match.Value));
            return $"[URL:{encoded}]";
        });

        result = IpRegex.Replace(result, match =>
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(match.Value));
            return $"[IP:{encoded}]";
        });

        return result;
    }
}
