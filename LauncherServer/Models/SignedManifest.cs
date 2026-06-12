using MessagePack;

namespace LauncherServer.Models;

/// <summary>
/// Signed manifest wrapper for Ed25519 verification
/// </summary>
[MessagePackObject]
public sealed record SignedManifest
{
    /// <summary>
    /// MessagePack-serialized manifest bytes
    /// </summary>
    [Key(0)]
    public required byte[] ManifestData { get; init; }

    /// <summary>
    /// Ed25519 signature of ManifestData
    /// </summary>
    [Key(1)]
    public required byte[] Signature { get; init; }

    /// <summary>
    /// Public key identifier (first 8 bytes of public key hash)
    /// </summary>
    [Key(2)]
    public required string KeyId { get; init; }

    /// <summary>
    /// Signature timestamp
    /// </summary>
    [Key(3)]
    public required DateTimeOffset SignedAt { get; init; }
}
