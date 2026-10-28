namespace StreamDeckPilot.Web;

/// <summary>
/// The web UI (#95). Off by default. When on, every page needs a signed-in user (#97, docs/auth.md);
/// the REST API and its key are unaffected either way.
/// </summary>
public sealed class WebOptions
{
    public const string Section = "Web";

    /// <summary><c>Web__Enabled=true</c> serves the UI under <c>/</c>. Default false: nothing UI-related is mapped.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The address people use to reach the UI, scheme and host only (e.g. <c>https://deck.example.com</c>;
    /// a sub-path comes from <c>Proxy:PathBase</c>). Required with an OpenID Connect provider:
    /// redirect URIs are built from it, never from request headers. When set, it is also the only
    /// origin the Blazor circuit and the live hub accept; without it, the request's own origin.
    /// </summary>
    public string? PublicOrigin { get; set; }

    /// <summary><see cref="PublicOrigin"/> as <c>scheme://host[:port]</c>, or null when unset or invalid.</summary>
    public string? NormalisedPublicOrigin =>
        string.IsNullOrWhiteSpace(PublicOrigin) ? null
        : Uri.TryCreate(PublicOrigin.Trim(), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;

    public string? Error()
    {
        if (string.IsNullOrWhiteSpace(PublicOrigin)) return null;
        if (!Uri.TryCreate(PublicOrigin.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return $"Web:PublicOrigin '{PublicOrigin}' must be a scheme and host only, e.g. https://deck.example.com (set a sub-path with Proxy:PathBase).";
        return null;
    }
}
