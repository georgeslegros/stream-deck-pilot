using System.Net;

namespace StreamDeckPilot.Api.Proxy;

/// <summary>
/// Running behind a reverse proxy (#109): which peers may set X-Forwarded-* headers, how many
/// proxy hops to unwind, and an optional path base for serving under a sub-path (e.g. /deck).
/// </summary>
public sealed class ProxyOptions
{
    public const string Section = "Proxy";

    /// <summary>Private and loopback ranges: a proxy on the same host, Docker network or LAN.</summary>
    public static readonly string[] DefaultTrustedNetworks =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "fc00::/7", "::1/128"];

    /// <summary>
    /// CIDR ranges whose peers may set forwarded headers. Null keeps the defaults. Arrays bind by
    /// replacement, so a configured list is the whole list, not an addition to the defaults.
    /// </summary>
    public string[]? TrustedNetworks { get; set; }

    /// <summary>Individual proxy addresses trusted in addition to <see cref="TrustedNetworks"/>.</summary>
    public string[]? TrustedProxies { get; set; }

    /// <summary>Proxy hops to unwind in X-Forwarded-For (1 = a single proxy in front).</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>Optional sub-path the app is served under, e.g. "/deck". Null or empty = root.</summary>
    public string? PathBase { get; set; }

    public IReadOnlyList<string> EffectiveTrustedNetworks => TrustedNetworks ?? DefaultTrustedNetworks;

    public string? NormalisedPathBase => string.IsNullOrWhiteSpace(PathBase) ? null : PathBase.Trim();

    /// <summary>The first configuration error, or null when the options are usable.</summary>
    public string? Error()
    {
        foreach (var network in EffectiveTrustedNetworks)
            if (!IPNetwork.TryParse(network, out _))
                return $"Proxy:TrustedNetworks entry '{network}' is not a CIDR range (e.g. 10.0.0.0/8).";
        foreach (var proxy in TrustedProxies ?? [])
            if (!IPAddress.TryParse(proxy, out _))
                return $"Proxy:TrustedProxies entry '{proxy}' is not an IP address.";
        if (ForwardLimit < 1)
            return "Proxy:ForwardLimit must be at least 1.";
        if (NormalisedPathBase is { } pathBase &&
            (pathBase == "/" || !pathBase.StartsWith('/') || pathBase.EndsWith('/') || pathBase.Contains("//")
             || pathBase.IndexOfAny(['?', '#', ' ']) >= 0))
            return $"Proxy:PathBase '{pathBase}' must start with '/', not end with '/', and contain no '?', '#', spaces or empty segments (e.g. /deck).";
        return null;
    }
}
