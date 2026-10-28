using System.Security.Cryptography;
using System.Text;

namespace StreamDeckPilot.Core.Security;

/// <summary>
/// A key that was accepted: its name (for audit and last-used tracking), whether it may only read,
/// and whether it is the bootstrap key from configuration (the only key that may run first-run setup).
/// </summary>
public sealed record ApiKeyIdentity(string Name, bool ReadOnly, bool IsBootstrap);

/// <summary>
/// Decides whether an API key is valid. Today one key from configuration (<c>ApiKey</c>), the
/// bootstrap key; named, revocable keys (#98) plug in here by replacing the registration.
/// </summary>
public interface IApiKeyValidator
{
    /// <summary>The key's identity when it is valid, otherwise null. Must not log the key.</summary>
    ApiKeyIdentity? Validate(string providedKey);

    /// <summary>Whether a bootstrap key is configured and enabled (first-run setup needs it).</summary>
    bool BootstrapKeyAvailable { get; }
}

/// <summary>
/// Compares secrets without leaking, through timing, how much of them matched or how long they are:
/// both sides are hashed with SHA-256 first, so the fixed-time comparison always covers 32 bytes.
/// </summary>
public static class SecretComparer
{
    public static bool Matches(string expected, string provided)
    {
        if (expected.Length == 0) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(a, b) & provided.Length > 0;
    }
}
