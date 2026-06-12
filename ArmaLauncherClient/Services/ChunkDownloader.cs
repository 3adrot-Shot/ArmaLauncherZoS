using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Channels;
using ArmaLauncherClient.Models;
using Polly;
using Polly.Retry;
using ZstdSharp;

namespace ArmaLauncherClient.Services;

/// <summary>
/// High-performance parallel chunk downloader
/// </summary>
public sealed class ChunkDownloader : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly DeduplicationCache _cache;
    private readonly CryptoService _crypto;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    private readonly int _maxConcurrency;
    private readonly int _maxRetries;
    private long _bytesDownloaded;
    private long _bandwidthLimit;

    private CancellationTokenSource? _cts;
    private bool _isPaused;

    public event Action<DownloadProgress>? ProgressChanged;
    public event Action<ChunkDownloadStatus>? ChunkStatusChanged;

    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);

    public ChunkDownloader(
        HttpClient httpClient,
        DeduplicationCache cache,
        CryptoService crypto,
        int maxConcurrency = 32,
        int maxRetries = 5)
    {
        _httpClient = httpClient;
        _cache = cache;
        _crypto = crypto;
        _maxConcurrency = maxConcurrency;
        _maxRetries = maxRetries;

        _retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = maxRetries,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(500),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(r => !r.IsSuccessStatusCode && (int)r.StatusCode >= 500)
            })
            .Build();
    }

    public void SetBandwidthLimit(long bytesPerSecond)
    {
        Interlocked.Exchange(ref _bandwidthLimit, bytesPerSecond);
    }

    public async Task<DownloadResult> DownloadManifestAsync(
        Manifest manifest,
        string baseUrl,
        CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _cts.Token;

        var allChunks = manifest.Files
            .SelectMany(f => f.Chunks)
            .DistinctBy(c => c.Hash)
            .ToList();

        var result = new DownloadResult
        {
            TotalChunks = allChunks.Count,
            TotalBytes = allChunks.Sum(c => (long)c.Size)
        };

        var chunksToDownload = new ConcurrentBag<ChunkInfo>();

        await Parallel.ForEachAsync(allChunks, linkedCt, async (chunk, ct2) =>
        {
            if (_cache.HasChunk(chunk.Hash))
            {
                Interlocked.Increment(ref result._cachedChunks);
                Interlocked.Add(ref result._cachedBytes, chunk.Size);
            }
            else
            {
                chunksToDownload.Add(chunk);
            }
        });

        ReportProgress(manifest, result, DownloadState.Downloading);

        var downloadQueue = Channel.CreateBounded<ChunkInfo>(new BoundedChannelOptions(_maxConcurrency * 2)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        var completedChunks = new ConcurrentDictionary<string, bool>();
        var failedChunks = new ConcurrentBag<(ChunkInfo Chunk, string Error)>();

        var producer = Task.Run(async () =>
        {
            foreach (var chunk in chunksToDownload)
            {
                await downloadQueue.Writer.WriteAsync(chunk, linkedCt);
            }
            downloadQueue.Writer.Complete();
        }, linkedCt);

        var consumers = Enumerable.Range(0, _maxConcurrency)
            .Select(_ => Task.Run(async () =>
            {
                await foreach (var chunk in downloadQueue.Reader.ReadAllAsync(linkedCt))
                {
                    while (_isPaused && !linkedCt.IsCancellationRequested)
                    {
                        await Task.Delay(100, linkedCt);
                    }

                    try
                    {
                        await DownloadChunkAsync(chunk, baseUrl, linkedCt);
                        completedChunks[chunk.Hash] = true;

                        Interlocked.Add(ref result._downloadedBytes, chunk.CompressedSize);
                        Interlocked.Increment(ref result._downloadedChunks);

                        ChunkStatusChanged?.Invoke(new ChunkDownloadStatus
                        {
                            Hash = chunk.Hash,
                            State = ChunkState.Completed
                        });

                        ReportProgress(manifest, result, DownloadState.Downloading);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failedChunks.Add((chunk, ex.Message));
                        Interlocked.Increment(ref result._failedChunks);

                        ChunkStatusChanged?.Invoke(new ChunkDownloadStatus
                        {
                            Hash = chunk.Hash,
                            State = ChunkState.Failed,
                            Error = ex.Message
                        });
                    }
                }
            }, linkedCt))
            .ToArray();

        await producer;
        await Task.WhenAll(consumers);

        result.Success = result.FailedChunks == 0;
        result.FailedChunkDetails = failedChunks.Select(f => (f.Chunk.Hash, f.Error)).ToList();

        ReportProgress(manifest, result, result.Success ? DownloadState.Completed : DownloadState.Failed);

        return result;
    }

    private async Task DownloadChunkAsync(ChunkInfo chunk, string baseUrl, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/chunks/{chunk.Hash}";

        ChunkStatusChanged?.Invoke(new ChunkDownloadStatus
        {
            Hash = chunk.Hash,
            State = ChunkState.Downloading
        });

        var response = await _retryPipeline.ExecuteAsync(async (ct2) =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{chunk.Hash}\""));

            var resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct2);

            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                return resp;
            }

            resp.EnsureSuccessStatusCode();
            return resp;
        }, ct);

        if (response.StatusCode == HttpStatusCode.NotModified)
            return;

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);

        byte[] compressedData;
        using (var ms = new MemoryStream())
        {
            var limit = Interlocked.Read(ref _bandwidthLimit);
            if (limit > 0)
            {
                await CopyWithThrottlingAsync(responseStream, ms, limit, ct);
            }
            else
            {
                await responseStream.CopyToAsync(ms, ct);
            }
            compressedData = ms.ToArray();
        }

        Interlocked.Add(ref _bytesDownloaded, compressedData.Length);

        byte[] decompressedData;
        if (chunk.Compression == CompressionType.Zstd)
        {
            using var decompressor = new Decompressor();
            decompressedData = decompressor.Unwrap(compressedData).ToArray();
        }
        else
        {
            decompressedData = compressedData;
        }

        var actualHash = CryptoService.HashSha256(decompressedData);
        if (!string.Equals(actualHash, chunk.Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Chunk hash mismatch. Expected: {chunk.Hash}, Actual: {actualHash}");
        }

        await _cache.StoreChunkAsync(chunk.Hash, decompressedData, ct);
    }

    private static async Task CopyWithThrottlingAsync(
        Stream source,
        Stream destination,
        long bytesPerSecond,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long totalBytes = 0;

            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                totalBytes += read;

                var expectedTime = TimeSpan.FromSeconds((double)totalBytes / bytesPerSecond);
                var actualTime = stopwatch.Elapsed;

                if (actualTime < expectedTime)
                {
                    await Task.Delay(expectedTime - actualTime, ct);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Pause() => _isPaused = true;
    public void Resume() => _isPaused = false;
    public void Cancel() => _cts?.Cancel();

    private void ReportProgress(Manifest manifest, DownloadResult result, DownloadState state)
    {
        ProgressChanged?.Invoke(new DownloadProgress
        {
            ModelId = manifest.ModelId,
            Version = manifest.Version,
            State = state,
            TotalBytes = result.TotalBytes,
            DownloadedBytes = result.DownloadedBytes,
            CachedBytes = result.CachedBytes,
            TotalChunks = result.TotalChunks,
            DownloadedChunks = result.DownloadedChunks,
            CachedChunks = result.CachedChunks,
            FailedChunks = result.FailedChunks
        });
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class DownloadResult
{
    public bool Success { get; set; }
    public int TotalChunks { get; set; }
    public long TotalBytes { get; set; }

    // Use fields for Interlocked operations
    internal int _downloadedChunks;
    internal long _downloadedBytes;
    internal int _cachedChunks;
    internal long _cachedBytes;
    internal int _failedChunks;

    public int DownloadedChunks => _downloadedChunks;
    public long DownloadedBytes => _downloadedBytes;
    public int CachedChunks => _cachedChunks;
    public long CachedBytes => _cachedBytes;
    public int FailedChunks => _failedChunks;

    public List<(string Hash, string Error)>? FailedChunkDetails { get; set; }
}
