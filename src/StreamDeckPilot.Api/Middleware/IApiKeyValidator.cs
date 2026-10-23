using System.Security.Cryptography;
using System.Text;

namespace StreamDeckPilot.Api.Middleware;

/// <summary>A key that was accepted: its name (for audit and last-used tracking) and whether it may only read.</summary>
public sealed record ApiKeyIdentity(string Name, bool ReadOnly);

/// <summary>
/// Decides whether an API key is valid. Today one key from configuration (<c>ApiKey</c>); named,
/// revocable keys (#98) plug in here by replacing the registration.
/// </summary>
public interface IApiKeyValidator
{
    /// <summary>The key's identity when it is valid, otherwise null. Must not log the key.</summary>
    ApiKeyIdentity? Validate(string providedKey);
}

/// <summary>The single key from the <c>ApiKey</c> setting (environment variable or Docker secret).</summary>
public sealed class ConfiguredApiKeyValidator(IConfiguration config) : IApiKeyValidator
{
    public static readonly ApiKeyIdentity Identity = new("configured", ReadOnly: false);
    private static readonly byte[] Invalid = Encoding.UTF8.GetBytes("__invalid__");

    public ApiKeyIdentity? Validate(string providedKey)
    {
        var expected = config["ApiKey"] ?? string.Empty;
        var expectedBytes = expected.Length > 0 ? Encoding.UTF8.GetBytes(expected) : Invalid;
        var providedBytes = Encoding.UTF8.GetBytes(providedKey.Length > 0 ? providedKey : "__missing__");

        // Constant-length comparison to resist timing attacks
        return expectedBytes.Length == providedBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes)
            ? Identity
            : null;
    }
}
