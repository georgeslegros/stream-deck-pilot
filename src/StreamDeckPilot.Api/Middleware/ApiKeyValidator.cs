using System.Security.Cryptography;
using StreamDeckPilot.Core.Security;
using StreamDeckPilot.Infrastructure.Security;

namespace StreamDeckPilot.Api.Middleware;

/// <summary>
/// Accepts the bootstrap key (the <c>ApiKey</c> setting, unless <c>Auth:BootstrapKeyEnabled</c> is
/// false) and named keys from <see cref="ApiKeyStore"/> (#98).
/// <para>
/// A named key is found by the id embedded in it, then its SHA-256 is compared with the stored hash
/// in fixed time. Every attempt does the same work whatever fails (unknown or malformed id, wrong
/// secret, revoked, expired): the comparison runs against a dummy hash when there's no record, and
/// the bootstrap comparison always runs too. Every failure is the same null, so one 401 for clients.
/// </para>
/// </summary>
public sealed class ApiKeyValidator(IConfiguration config, ApiKeyStore store, TimeProvider time) : IApiKeyValidator
{
    public static readonly ApiKeyIdentity Bootstrap = new(ApiKeyStore.ReservedName, ReadOnly: false, IsBootstrap: true);

    private static readonly byte[] DummyHash = SHA256.HashData("no such key"u8);

    /// <summary>Off with <c>Auth:BootstrapKeyEnabled=false</c>: then only named keys and sessions work, and first-run setup can't run.</summary>
    public bool BootstrapKeyEnabled => config.GetValue("Auth:BootstrapKeyEnabled", true);

    public bool BootstrapKeyAvailable => BootstrapKeyEnabled && !string.IsNullOrEmpty(config["ApiKey"]);

    public ApiKeyIdentity? Validate(string providedKey)
    {
        var named = ValidateNamed(providedKey);
        var bootstrap = SecretComparer.Matches(BootstrapKeyEnabled ? config["ApiKey"] ?? "" : "", providedKey);
        return named ?? (bootstrap ? Bootstrap : null);
    }

    private ApiKeyIdentity? ValidateNamed(string providedKey)
    {
        var id = ApiKeyFormat.IdOf(providedKey);
        var record = id is null ? null : store.Find(id);
        var expected = record is null ? DummyHash : Convert.FromHexString(record.Hash);
        var matches = CryptographicOperations.FixedTimeEquals(ApiKeyFormat.HashBytes(providedKey), expected);

        if (!matches || record is null || !record.IsActive(time.GetUtcNow())) return null;
        store.Touch(record.Id);
        return new ApiKeyIdentity(record.Name, record.Scope == ApiKeyScope.ReadOnly, IsBootstrap: false, record.Id, record.ExpiresAt);
    }
}
