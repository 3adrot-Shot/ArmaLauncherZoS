using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace ArmaLauncherClient.Services;

/// <summary>
/// Local chunk deduplication cache
/// </summary>
public sealed class DeduplicationCache : IDisposable
{
    private readonly string _cachePath;
    private readonly string _indexPath;
    private readonly ConcurrentDictionary<string, CacheEntry> _index;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Timer _saveTimer;
    private bool _dirty;

    public string CachePath => _cachePath;

    public DeduplicationCache(string? cachePath = null)
    {
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AUModelLauncher", "cache", "chunks");

        _indexPath = Path.Combine(_cachePath, "index.json");

        Directory.CreateDirectory(_cachePath);

        _index = LoadIndex();

        // Auto-save index every 30 seconds if dirty
        _saveTimer = new Timer(_ => SaveIndexIfDirty(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Check if a chunk exists in cache
    /// </summary>
    public bool HasChunk(string hash)
    {
        if (!_index.TryGetValue(hash, out var entry))
            return false;

        var path = GetChunkPath(hash);
        if (!File.Exists(path))
        {
            _index.TryRemove(hash, out _);
            _dirty = true;
            return false;
        }

        // Update access time
        entry.LastAccessed = DateTimeOffset.UtcNow;
        entry.AccessCount++;
        _dirty = true;

        return true;
    }

    /// <summary>
    /// Get chunk data from cache
    /// </summary>
    public async Task<byte[]?> GetChunkAsync(string hash, CancellationToken ct = default)
    {
        if (!HasChunk(hash))
            return null;

        var path = GetChunkPath(hash);
        return await File.ReadAllBytesAsync(path, ct);
    }

    /// <summary>
    /// Get chunk stream from cache
    /// </summary>
    public Stream? GetChunkStream(string hash)
    {
        if (!HasChunk(hash))
            return null;

        var path = GetChunkPath(hash);
        return File.OpenRead(path);
    }

    /// <summary>
    /// Store chunk in cache (already decompressed)
    /// </summary>
    public async Task StoreChunkAsync(string hash, byte[] data, CancellationToken ct = default)
    {
        var path = GetChunkPath(hash);
        var dir = Path.GetDirectoryName(path)!;

        await _writeLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(path, data, ct);

            _index[hash] = new CacheEntry
            {
                Hash = hash,
                Size = data.Length,
                CreatedAt = DateTimeOffset.UtcNow,
                LastAccessed = DateTimeOffset.UtcNow,
                AccessCount = 1
            };
            _dirty = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Get total cache size
    /// </summary>
    public long GetCacheSize() => _index.Values.Sum(e => (long)e.Size);

    /// <summary>
    /// Get chunk count
    /// </summary>
    public int GetChunkCount() => _index.Count;

    /// <summary>
    /// Evict least recently used chunks to free space
    /// </summary>
    public async Task EvictToSizeAsync(long maxBytes, CancellationToken ct = default)
    {
        var currentSize = GetCacheSize();
        if (currentSize <= maxBytes)
            return;

        var toEvict = _index.Values
            .OrderBy(e => e.LastAccessed)
            .ThenBy(e => e.AccessCount)
            .ToList();

        foreach (var entry in toEvict)
        {
            if (currentSize <= maxBytes)
                break;

            ct.ThrowIfCancellationRequested();

            var path = GetChunkPath(entry.Hash);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    currentSize -= entry.Size;
                    _index.TryRemove(entry.Hash, out _);
                    _dirty = true;
                }
                catch
                {
                    // Ignore deletion errors
                }
            }
        }

        await SaveIndexAsync(ct);
    }

    /// <summary>
    /// Verify cache integrity
    /// </summary>
    public async IAsyncEnumerable<(string Hash, bool Valid)> VerifyIntegrityAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in _index.Values.ToList())
        {
            ct.ThrowIfCancellationRequested();

            var path = GetChunkPath(entry.Hash);
            if (!File.Exists(path))
            {
                _index.TryRemove(entry.Hash, out _);
                _dirty = true;
                yield return (entry.Hash, false);
                continue;
            }

            var data = await File.ReadAllBytesAsync(path, ct);
            var actualHash = CryptoService.HashSha256(data);
            var valid = string.Equals(actualHash, entry.Hash, StringComparison.OrdinalIgnoreCase);

            if (!valid)
            {
                File.Delete(path);
                _index.TryRemove(entry.Hash, out _);
                _dirty = true;
            }

            yield return (entry.Hash, valid);
        }
    }

    private string GetChunkPath(string hash)
    {
        // Spread chunks across directories using first 4 chars of hash
        return Path.Combine(_cachePath, hash[..2], hash[2..4], hash);
    }

    private ConcurrentDictionary<string, CacheEntry> LoadIndex()
    {
        if (!File.Exists(_indexPath))
            return new ConcurrentDictionary<string, CacheEntry>();

        try
        {
            var json = File.ReadAllText(_indexPath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json) ?? [];
            return new ConcurrentDictionary<string, CacheEntry>(
                entries.ToDictionary(e => e.Hash, e => e));
        }
        catch
        {
            return new ConcurrentDictionary<string, CacheEntry>();
        }
    }

    private void SaveIndexIfDirty()
    {
        if (!_dirty) return;
        try
        {
            SaveIndexAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore save errors in background
        }
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var entries = _index.Values.ToList();
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_indexPath, json, ct);
            _dirty = false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveIndexIfDirty();
        _writeLock.Dispose();
    }
}

public sealed class CacheEntry
{
    public required string Hash { get; init; }
    public int Size { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastAccessed { get; set; }
    public int AccessCount { get; set; }
}
