using System.IO;
using LauncherServer.Models;
using MessagePack;

namespace LauncherServer.Services;

/// <summary>
/// Creates and signs model manifests
/// </summary>
public sealed class ManifestService
{
    private readonly CryptoService _cryptoService;
    private readonly ILogger<ManifestService> _logger;

    public ManifestService(
        CryptoService cryptoService,
        ILogger<ManifestService> logger)
    {
        _cryptoService = cryptoService;
        _logger = logger;
    }

    /// <summary>
    /// Create a signed manifest for a model
    /// </summary>
    public SignedManifest CreateSignedManifest(string modelId, string version, List<FileEntry> files, long totalSize)
    {
        var manifest = new Manifest
        {
            ModelId = modelId,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            TotalSize = totalSize,
            TotalCompressedSize = totalSize,
            Files = files,
            FormatVersion = 1
        };

        return SignManifest(manifest);
    }

    /// <summary>
    /// Sign a manifest with Ed25519
    /// </summary>
    public SignedManifest SignManifest(Manifest manifest)
    {
        var manifestData = MessagePackSerializer.Serialize(manifest);
        var signature = _cryptoService.Sign(manifestData);

        return new SignedManifest
        {
            ManifestData = manifestData,
            Signature = signature,
            KeyId = _cryptoService.KeyId,
            SignedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Verify and deserialize a signed manifest
    /// </summary>
    public Manifest? VerifyAndDeserialize(SignedManifest signedManifest)
    {
        if (!_cryptoService.Verify(signedManifest.ManifestData, signedManifest.Signature))
        {
            _logger.LogWarning("Manifest signature verification failed");
            return null;
        }

        return MessagePackSerializer.Deserialize<Manifest>(signedManifest.ManifestData);
    }

    private static FileType DetectFileType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".gguf" => FileType.Gguf,
            ".safetensors" => FileType.SafeTensors,
            ".pth" or ".pt" or ".bin" => FileType.Pth,
            ".json" => FileType.Json,
            ".txt" or ".md" => FileType.Text,
            _ => FileType.Binary
        };
    }
}
