using StreamDeckPilot.Core.Security;

namespace StreamDeckPilot.Api.Middleware;

/// <summary>The single key from the <c>ApiKey</c> setting (environment variable or Docker secret): the bootstrap key.</summary>
public sealed class ConfiguredApiKeyValidator(IConfiguration config) : IApiKeyValidator
{
    public static readonly ApiKeyIdentity Identity = new("bootstrap", ReadOnly: false, IsBootstrap: true);

    public bool BootstrapKeyAvailable => !string.IsNullOrEmpty(config["ApiKey"]);

    public ApiKeyIdentity? Validate(string providedKey) =>
        SecretComparer.Matches(config["ApiKey"] ?? string.Empty, providedKey) ? Identity : null;
}
