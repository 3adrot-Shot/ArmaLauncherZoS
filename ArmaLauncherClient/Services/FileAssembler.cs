using System.IO;
using System.IO.MemoryMappedFiles;
using ArmaLauncherClient.Models;

namespace ArmaLauncherClient.Services;

/// <summary>
/// Assembles final model files from cached chunks using memory-mapped files
/// </summary>
public sealed class FileAssembler
{
    private readonly DeduplicationCache _cache;
    private readonly CryptoService _crypto;

    public event Action<string, double>? ProgressChanged;

    public FileAssembler(DeduplicationCache cache, CryptoService crypto)
    {
        _cache = cache;
        _crypto = crypto;
    }

    /// <summary>
    /// Assemble all files from a manifest
    /// </summary>
    public async Task AssembleManifestAsync(
        Manifest manifest,
        string outputDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var totalFiles = manifest.Files.Count;
        var currentFile = 0;

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            var outputPath = Path.Combine(outputDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var outputDir = Path.GetDirectoryName(outputPath)!;
            Directory.CreateDirectory(outputDir);

            await AssembleFileAsync(file, outputPath, ct);

            currentFile++;
            ProgressChanged?.Invoke(file.Path, (double)currentFile / totalFiles * 100);
        }
    }

    /// <summary>
    /// Assemble a single file from chunks
    /// </summary>
    public async Task AssembleFileAsync(FileEntry file, string outputPath, CancellationToken ct = default)
    {
        // For very large files, use memory-mapped files
        if (file.Size > 100 * 1024 * 1024) // > 100MB
        {
            await AssembleWithMemoryMappedFileAsync(file, outputPath, ct);
        }
        else
        {
            await AssembleWithStreamsAsync(file, outputPath, ct);
        }

        // Verify final file hash
        await VerifyFileAsync(file, outputPath, ct);
    }

    /// <summary>
    /// Assemble using memory-mapped files for large files
    /// </summary>
    private async Task AssembleWithMemoryMappedFileAsync(
        FileEntry file,
        string outputPath,
        CancellationToken ct)
    {
        // Pre-allocate file
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        fs.SetLength(file.Size);
        fs.Close();

        using var mmf = MemoryMappedFile.CreateFromFile(
            outputPath,
            FileMode.Open,
            null,
            file.Size,
            MemoryMappedFileAccess.ReadWrite);

        // Process chunks in parallel batches for better I/O
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(file.Chunks, parallelOptions, async (chunk, ct2) =>
        {
            var chunkData = await _cache.GetChunkAsync(chunk.Hash, ct2)
                ?? throw new InvalidOperationException($"Chunk {chunk.Hash} not found in cache");

            // Write directly to memory-mapped region
            using var accessor = mmf.CreateViewAccessor(chunk.Offset, chunk.Size, MemoryMappedFileAccess.Write);
            accessor.WriteArray(0, chunkData, 0, chunkData.Length);
        });
    }

    /// <summary>
    /// Assemble using regular streams for smaller files
    /// </summary>
    private async Task AssembleWithStreamsAsync(
        FileEntry file,
        string outputPath,
        CancellationToken ct)
    {
        await using var outputStream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024, // 1MB buffer
            useAsync: true);

        // Chunks must be written in order
        foreach (var chunk in file.Chunks.OrderBy(c => c.Offset))
        {
            ct.ThrowIfCancellationRequested();

            var chunkData = await _cache.GetChunkAsync(chunk.Hash, ct)
                ?? throw new InvalidOperationException($"Chunk {chunk.Hash} not found in cache");

            await outputStream.WriteAsync(chunkData, ct);
        }

        await outputStream.FlushAsync(ct);
    }

    /// <summary>
    /// Verify assembled file matches expected hash
    /// </summary>
    private async Task VerifyFileAsync(FileEntry file, string outputPath, CancellationToken ct)
    {
        await using var fileStream = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);

        var actualHash = await CryptoService.HashSha256Async(fileStream, ct);

        if (!string.Equals(actualHash, file.Hash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(outputPath);
            throw new InvalidDataException(
                $"File hash mismatch for {file.Path}. Expected: {file.Hash}, Actual: {actualHash}");
        }
    }

    /// <summary>
    /// Check if file already exists and is valid
    /// </summary>
    public async Task<bool> IsFileValidAsync(FileEntry file, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(outputPath))
            return false;

        var info = new FileInfo(outputPath);
        if (info.Length != file.Size)
            return false;

        await using var fileStream = File.OpenRead(outputPath);
        var actualHash = await CryptoService.HashSha256Async(fileStream, ct);

        return string.Equals(actualHash, file.Hash, StringComparison.OrdinalIgnoreCase);
    }
}
