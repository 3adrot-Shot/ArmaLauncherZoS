using System.IO;
using LauncherServer.Models;

namespace LauncherServer.Services;

/// <summary>
/// Generates and applies binary delta patches
/// Note: In production, integrate with a proper bsdiff library
/// </summary>
public sealed class DeltaPatchService
{
    private readonly ILogger<DeltaPatchService> _logger;
    private readonly CryptoService _cryptoService;

    public DeltaPatchService(ILogger<DeltaPatchService> logger, CryptoService cryptoService)
    {
        _logger = logger;
        _cryptoService = cryptoService;
    }

    /// <summary>
    /// Generate a simple delta patch (placeholder - in production use bsdiff)
    /// For now, just returns the new file as the patch
    /// </summary>
    public async Task<PatchResult> GeneratePatchAsync(
        string oldFilePath,
        string newFilePath,
        string fromVersion,
        string toVersion,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Generating patch: {OldFile} -> {NewFile}", oldFilePath, newFilePath);

        var newData = await File.ReadAllBytesAsync(newFilePath, ct);
        var patchHash = CryptoService.HashSha256(newData);

        var relativePath = Path.GetFileName(newFilePath);

        var patchInfo = new PatchInfo
        {
            FromVersion = fromVersion,
            ToVersion = toVersion,
            PatchSize = newData.Length,
            PatchHash = patchHash,
            FilePath = relativePath,
            Algorithm = PatchAlgorithm.BsDiff4
        };

        _logger.LogInformation("Generated patch: {Size:N0} bytes", newData.Length);

        return new PatchResult
        {
            Info = patchInfo,
            Data = newData
        };
    }

    /// <summary>
    /// Apply a delta patch (placeholder - in production use bspatch)
    /// </summary>
    public Task<byte[]> ApplyPatchAsync(
        byte[] oldData,
        byte[] patchData,
        string expectedHash,
        CancellationToken ct = default)
    {
        // Placeholder: just return the patch data as the result
        var actualHash = CryptoService.HashSha256(patchData);

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Patch result hash mismatch. Expected: {expectedHash}, Actual: {actualHash}");
        }

        return Task.FromResult(patchData);
    }

    public PatchAnalysis AnalyzePatchPotential(long oldSize, long newSize, long patchSize)
    {
        var fullDownloadSize = newSize;
        var patchDownloadSize = patchSize;
        var savingsRatio = 1.0 - ((double)patchDownloadSize / fullDownloadSize);
        var worthwhile = savingsRatio > 0.1;

        return new PatchAnalysis
        {
            FullDownloadSize = fullDownloadSize,
            PatchDownloadSize = patchDownloadSize,
            SavingsBytes = fullDownloadSize - patchDownloadSize,
            SavingsRatio = savingsRatio,
            Worthwhile = worthwhile
        };
    }
}

public sealed record PatchResult
{
    public required PatchInfo Info { get; init; }
    public required byte[] Data { get; init; }
}

public sealed record PatchAnalysis
{
    public long FullDownloadSize { get; init; }
    public long PatchDownloadSize { get; init; }
    public long SavingsBytes { get; init; }
    public double SavingsRatio { get; init; }
    public bool Worthwhile { get; init; }
}
