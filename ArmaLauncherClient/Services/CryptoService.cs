using System.IO;
using System.Security.Cryptography;

namespace ArmaLauncherClient.Services;

/// <summary>
/// Client-side cryptography - simplified version without signature verification
/// </summary>
public sealed class CryptoService
{
    /// <summary>
    /// Add a trusted public key (no-op in simplified version)
    /// </summary>
    public void AddTrustedKey(string keyId, byte[] publicKeyBytes)
    {
        // Simplified: no signature verification
    }

    /// <summary>
    /// Load trusted public key from file (no-op in simplified version)
    /// </summary>
    public void LoadTrustedKey(string path)
    {
        // Simplified: no signature verification
    }

    /// <summary>
    /// Verify signature - always returns true in simplified version
    /// </summary>
    public bool Verify(string keyId, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        // Simplified: always trust
        return true;
    }

    /// <summary>
    /// Compute SHA256 hash
    /// </summary>
    public static string HashSha256(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute SHA256 hash of a stream
    /// </summary>
    public static async Task<string> HashSha256Async(Stream stream, CancellationToken ct = default)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Verify hash of data
    /// </summary>
    public static bool VerifyHash(ReadOnlySpan<byte> data, string expectedHash)
    {
        var actualHash = HashSha256(data);
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
