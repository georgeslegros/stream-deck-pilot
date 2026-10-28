using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace StreamDeckPilot.Infrastructure.Security;

/// <summary>
/// Named API keys (#98) look like <c>sdp_&lt;id&gt;_&lt;secret&gt;</c>: a recognisable prefix (secret
/// scanners and people spot it), a 12-character public id (hex) that finds the stored record without
/// scanning, and 32 random bytes as base64url. Only a SHA-256 hash of the whole key is stored.
/// <para>
/// No slow KDF (PBKDF2, Argon2) is needed: those protect low-entropy passwords against guessing. A
/// key holds 256 random bits, so even with the hash in hand nobody can search that space, however
/// fast the hash is; and every API request checks a key, so a deliberately slow hash would only
/// slow the service down.
/// </para>
/// </summary>
public static class ApiKeyFormat
{
    public const string Prefix = "sdp_";
    public const int IdLength = 12;
    private const int SecretBytes = 32;

    /// <summary>A new key and its id.</summary>
    public static (string Id, string Key) Generate()
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(IdLength / 2));
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
        return (id, $"{Prefix}{id}_{secret}");
    }

    /// <summary>The id embedded in a named key, or null when the text isn't shaped like one.</summary>
    public static string? IdOf(string key)
    {
        // Fixed positions: the secret's base64url alphabet includes '_' and '-', so don't split on '_'.
        if (key.Length <= Prefix.Length + IdLength + 1 || !key.StartsWith(Prefix, StringComparison.Ordinal) ||
            key[Prefix.Length + IdLength] != '_')
            return null;
        var id = key.Substring(Prefix.Length, IdLength);
        return id.All(char.IsAsciiHexDigitLower) ? id : null;
    }

    /// <summary>The stored form: SHA-256 of the whole key, lower-case hex.</summary>
    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static byte[] HashBytes(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
