namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// Sign-in for the web UI (#97): OpenID Connect providers and a local account fallback. Only used
/// when <c>Web:Enabled</c> is true; the UI then always requires a signed-in user.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>OpenID Connect providers; the sign-in page shows one button per provider.</summary>
    public List<OidcProviderOptions> Providers { get; set; } = [];

    public LocalAccountOptions Local { get; set; } = new();

    /// <summary>How long a session lasts without activity. Sliding: each request renews it once half has passed.</summary>
    public double SessionHours { get; set; } = 12;

    /// <summary>Sign-in and setup attempts allowed per client address within <see cref="AttemptWindowSeconds"/>.</summary>
    public int AttemptLimit { get; set; } = 5;

    public int AttemptWindowSeconds { get; set; } = 300;

    /// <summary>Local accounts are on when configured so, or automatically when no provider is configured.</summary>
    public bool LocalEnabled => Local.Enabled ?? Providers.Count == 0;

    /// <summary>The first configuration error, or null when the options are usable.</summary>
    public string? Error(string? publicOrigin)
    {
        if (!LocalEnabled && Providers.Count == 0)
            return "Auth: no way to sign in. Configure an OpenID Connect provider (Auth:Providers) or enable local accounts (Auth:Local:Enabled).";
        if (SessionHours is <= 0 or > 24 * 90)
            return "Auth:SessionHours must be between 0 and 2160 (90 days).";
        if (AttemptLimit < 1 || AttemptWindowSeconds < 1)
            return "Auth:AttemptLimit and Auth:AttemptWindowSeconds must be at least 1.";
        if (Providers.Count > 0 && publicOrigin is null)
            return "Web:PublicOrigin is required when an OpenID Connect provider is configured: redirect URIs are built from it, never from request headers.";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Providers)
        {
            if (p.Error() is { } error) return error;
            if (!names.Add(p.Name)) return $"Auth:Providers: the name '{p.Name}' is used twice.";
        }
        return null;
    }
}

public sealed class LocalAccountOptions
{
    /// <summary>Null (default): on when no provider is configured, so a fresh install is never locked out.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Minimum length of a local password.</summary>
    public int MinimumPasswordLength { get; set; } = 12;
}

/// <summary>One OpenID Connect provider (Entra ID, Keycloak, Authentik, Authelia, Zitadel, Google…).</summary>
public sealed class OidcProviderOptions
{
    /// <summary>Short id used in URLs: letters, digits and dashes (e.g. <c>keycloak</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Button label on the sign-in page. Defaults to <see cref="Name"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The issuer URL; its <c>/.well-known/openid-configuration</c> is read at first use.</summary>
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    /// <summary>Pass it as an environment variable or Docker secret, never in a committed file.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Scopes to request. Null: <c>openid profile email</c>.</summary>
    public string[]? Scopes { get; set; }

    /// <summary>
    /// The claim that carries roles or groups, from the ID token or the user-info response. A dotted
    /// path reads inside a JSON claim, e.g. <c>realm_access.roles</c> for Keycloak realm roles.
    /// </summary>
    public string RoleClaim { get; set; } = "roles";

    /// <summary>Values of <see cref="RoleClaim"/> that make a user an admin. Null: <c>admin</c>.</summary>
    public string[]? AdminValues { get; set; }

    /// <summary>Values of <see cref="RoleClaim"/> that make a user a viewer. Null: <c>viewer</c>.</summary>
    public string[]? ViewerValues { get; set; }

    /// <summary>
    /// Role for a user who matches neither list: null (default) refuses them, <c>viewer</c> lets
    /// everyone the provider signs in look but not change anything.
    /// </summary>
    public string? DefaultRole { get; set; }

    /// <summary>Require HTTPS for the provider's metadata. Only turn off for a local test provider.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    // Arrays are nullable with defaults applied here: the configuration binder would otherwise
    // append configured entries to a default array instead of replacing it.
    public IReadOnlyList<string> EffectiveScopes => Scopes is { Length: > 0 } ? Scopes : ["openid", "profile", "email"];
    public IReadOnlyList<string> EffectiveAdminValues => AdminValues ?? [AuthRoles.Admin];
    public IReadOnlyList<string> EffectiveViewerValues => ViewerValues ?? [AuthRoles.Viewer];

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public string Scheme => $"oidc-{Name}";
    public string CallbackPath => $"/signin-oidc-{Name}";

    public string? Error()
    {
        if (string.IsNullOrWhiteSpace(Name) || !Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return $"Auth:Providers: name '{Name}' must be letters, digits and dashes.";
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority) || authority.Scheme is not ("https" or "http"))
            return $"Auth:Providers:{Name}: Authority must be an absolute URL.";
        if (RequireHttpsMetadata && authority.Scheme != "https")
            return $"Auth:Providers:{Name}: Authority must use https (RequireHttpsMetadata).";
        if (string.IsNullOrWhiteSpace(ClientId))
            return $"Auth:Providers:{Name}: ClientId is required.";
        if (DefaultRole is not null && !AuthRoles.IsKnown(DefaultRole))
            return $"Auth:Providers:{Name}: DefaultRole must be '{AuthRoles.Admin}', '{AuthRoles.Viewer}' or unset.";
        return null;
    }
}
