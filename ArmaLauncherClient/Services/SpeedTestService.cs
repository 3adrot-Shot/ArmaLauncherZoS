using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Collections.Concurrent;

namespace ArmaLauncherClient.Services;

/// <summary>
/// Multi-threaded speed test service similar to Speedtest.net
/// </summary>
public class SpeedTestService
{
    private readonly HttpClient _httpClient;
    private const int TestDurationSeconds = 10;
    private const int ParallelStreams = 8; // Multiple parallel download streams
    
    public SpeedTestService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public record SpeedTestResult
    {
        public required string ServerUrl { get; init; }
        public double PingMs { get; init; }
        public double PingJitter { get; init; }
        public double DownloadSpeedMbps { get; init; }
        public double DownloadSpeedMBps { get; init; }
        public long BytesDownloaded { get; init; }
        public double TestDurationSeconds { get; init; }
        public int ParallelStreams { get; init; }
        public bool Success { get; init; }
        public string? Error { get; init; }
        
        public string Summary => Success 
            ? $"Ping: {PingMs:F0}ms (±{PingJitter:F0}ms) | Download: {DownloadSpeedMBps:F1} MB/s ({DownloadSpeedMbps:F0} Mbps) | {ParallelStreams} streams"
            : $"Failed: {Error}";
    }

    /// <summary>
    /// Run a complete speed test to the server
    /// </summary>
    public async Task<SpeedTestResult> RunSpeedTestAsync(string serverUrl, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var baseUrl = serverUrl.TrimEnd('/');
        
        try
        {
            // Step 1: Ping test (multiple samples for jitter calculation)
            progress?.Report("Измерение пинга...");
            var (pingMs, jitter) = await MeasurePingAsync(baseUrl, ct);
            FileLogger.Log($"SpeedTest: Ping = {pingMs:F1}ms, Jitter = {jitter:F1}ms");
            
            // Step 2: Multi-threaded download test
            progress?.Report($"Тест скорости ({ParallelStreams} потоков)...");
            var (downloadSpeed, bytesDownloaded, duration) = await MeasureDownloadSpeedMultiThreadedAsync(baseUrl, progress, ct);
            
            FileLogger.Log($"SpeedTest: Download = {downloadSpeed:F2} MB/s ({bytesDownloaded / 1024 / 1024} MB in {duration:F1}s, {ParallelStreams} streams)");
            
            return new SpeedTestResult
            {
                ServerUrl = baseUrl,
                PingMs = pingMs,
                PingJitter = jitter,
                DownloadSpeedMbps = downloadSpeed * 8,
                DownloadSpeedMBps = downloadSpeed,
                BytesDownloaded = bytesDownloaded,
                TestDurationSeconds = duration,
                ParallelStreams = ParallelStreams,
                Success = true
            };
        }
        catch (Exception ex)
        {
            FileLogger.Error("SpeedTest failed", ex);
            return new SpeedTestResult
            {
                ServerUrl = baseUrl,
                PingMs = 0,
                PingJitter = 0,
                DownloadSpeedMbps = 0,
                DownloadSpeedMBps = 0,
                BytesDownloaded = 0,
                TestDurationSeconds = 0,
                ParallelStreams = 0,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private async Task<(double AvgPing, double Jitter)> MeasurePingAsync(string baseUrl, CancellationToken ct)
    {
        var uri = new Uri(baseUrl);
        var host = uri.Host;
        var samples = new List<double>();
        
        // Take 10 ping samples
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();
            
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 2000);
                
                if (reply.Status == IPStatus.Success)
                {
                    samples.Add(reply.RoundtripTime);
                }
            }
            catch
            {
                // Fallback: HTTP ping
                var sw = Stopwatch.StartNew();
                try
                {
                    using var response = await _httpClient.GetAsync($"{baseUrl}/health", ct);
                    sw.Stop();
                    if (response.IsSuccessStatusCode)
                        samples.Add(sw.ElapsedMilliseconds);
                }
                catch { }
            }
            
            await Task.Delay(50, ct);
        }
        
        if (samples.Count == 0)
            return (0, 0);
        
        var avg = samples.Average();
        // Jitter = average deviation from mean
        var jitter = samples.Select(s => Math.Abs(s - avg)).Average();
        
        return (avg, jitter);
    }

    private async Task<(double SpeedMBps, long BytesDownloaded, double DurationSeconds)> MeasureDownloadSpeedMultiThreadedAsync(
        string baseUrl, IProgress<string>? progress, CancellationToken ct)
    {
        var totalBytes = new ConcurrentBag<long>();
        var sw = Stopwatch.StartNew();
        var testDuration = TimeSpan.FromSeconds(TestDurationSeconds);
        
        // Create cancellation that stops after test duration
        using var timeCts = new CancellationTokenSource(testDuration);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeCts.Token);
        
        // Start multiple parallel download streams
        var tasks = new List<Task>();
        
        for (int i = 0; i < ParallelStreams; i++)
        {
            var streamId = i;
            tasks.Add(Task.Run(async () =>
            {
                await DownloadStreamAsync(baseUrl, streamId, totalBytes, linkedCts.Token);
            }));
        }
        
        // Progress reporting task
        var progressTask = Task.Run(async () =>
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                await Task.Delay(200);
                var currentBytes = totalBytes.Sum();
                var elapsed = sw.Elapsed.TotalSeconds;
                if (elapsed > 0.1)
                {
                    var speed = currentBytes / elapsed / 1024 / 1024;
                    progress?.Report($"Тест: {speed:F1} MB/s ({currentBytes / 1024 / 1024} MB, {ParallelStreams} потоков)");
                }
            }
        }, linkedCts.Token);
        
        // Wait for all downloads to complete or timeout
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        
        sw.Stop();
        
        var totalBytesDownloaded = totalBytes.Sum();
        var duration = sw.Elapsed.TotalSeconds;
        var speedMBps = totalBytesDownloaded / duration / 1024 / 1024;
        
        return (speedMBps, totalBytesDownloaded, duration);
    }

    private async Task DownloadStreamAsync(string baseUrl, int streamId, ConcurrentBag<long> totalBytes, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024]; // 64KB buffer
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Request speed test data (10MB chunks)
                var url = $"{baseUrl}/speedtest";
                
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                
                if (!response.IsSuccessStatusCode)
                {
                    // Fallback to smaller requests
                    url = $"{baseUrl}/info";
                    using var fallbackResponse = await _httpClient.GetAsync(url, ct);
                    var data = await fallbackResponse.Content.ReadAsByteArrayAsync(ct);
                    totalBytes.Add(data.Length);
                    continue;
                }
                
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    totalBytes.Add(read);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"SpeedTest stream {streamId} error: {ex.Message}");
                await Task.Delay(100, ct);
            }
        }
    }
}
