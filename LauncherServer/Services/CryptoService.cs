using System.IO;
using System.Security.Cryptography;
using NSec.Cryptography;

namespace LauncherServer.Services;

/// <summary>
/// Ed25519 signing and SHA256 hashing service
/// </summary>
public sealed class CryptoService : IDisposable
{
    private readonly Key _signingKey;
    private readonly PublicKey _publicKey;
    private readonly string _keyId;

    public string KeyId => _keyId;
    public ReadOnlySpan<byte> PublicKeyBytes => _publicKey.Export(KeyBlobFormat.RawPublicKey);

    public CryptoService(string? privateKeyPath = null)
    {
        var algorithm = SignatureAlgorithm.Ed25519;

        if (!string.IsNullOrEmpty(privateKeyPath) && File.Exists(privateKeyPath))
        {
            var keyBytes = File.ReadAllBytes(privateKeyPath);
            _signingKey = Key.Import(algorithm, keyBytes, KeyBlobFormat.RawPrivateKey);
        }
        else
        {
            _signingKey = Key.Create(algorithm, new KeyCreationParameters
            {
                ExportPolicy = KeyExportPolicies.AllowPlaintextExport
            });

            if (!string.IsNullOrEmpty(privateKeyPath))
            {
                var dir = Path.GetDirectoryName(privateKeyPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllBytes(privateKeyPath, _signingKey.Export(KeyBlobFormat.RawPrivateKey)!);
            }
        }

        _publicKey = _signingKey.PublicKey;
        _keyId = ComputeKeyId(_publicKey);
    }

    private static string ComputeKeyId(PublicKey publicKey)
    {
        var pkBytes = publicKey.Export(KeyBlobFormat.RawPublicKey)!;
        var hash = SHA256.HashData(pkBytes);
        return Convert.ToHexString(hash[..8]);
    }

    /// <summary>
    /// Sign data with Ed25519
    /// </summary>
    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        return algorithm.Sign(_signingKey, data);
    }

    /// <summary>
    /// Verify Ed25519 signature
    /// </summary>
    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        return algorithm.Verify(_publicKey, data, signature);
    }

    /// <summary>
    /// Compute SHA256 hash of data
    /// </summary>
    public static string HashSha256(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute SHA256 hash of stream
    /// </summary>
    public static async Task<string> HashSha256Async(Stream stream, CancellationToken ct = default)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Export public key for distribution
    /// </summary>
    public void ExportPublicKey(string path)
    {
        File.WriteAllBytes(path, _publicKey.Export(KeyBlobFormat.RawPublicKey)!);
    }

    public void Dispose()
    {
        _signingKey.Dispose();
    }
}
