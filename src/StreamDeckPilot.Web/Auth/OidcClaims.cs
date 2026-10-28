using System.Security.Claims;
using System.Text.Json;

namespace StreamDeckPilot.Web.Auth;

/// <summary>Turns a provider's claims into the app's user: a display name and one role (#97).</summary>
public static class OidcClaims
{
    /// <summary>
    /// <c>admin</c> if any value of the role claim is in <see cref="OidcProviderOptions.AdminValues"/>,
    /// else <c>viewer</c> if any is in <see cref="OidcProviderOptions.ViewerValues"/>, else the
    /// provider's <see cref="OidcProviderOptions.DefaultRole"/> (null = refused). Values compare
    /// case-insensitively.
    /// </summary>
    public static string? Role(OidcProviderOptions provider, ClaimsPrincipal principal)
    {
        var values = RoleValues(provider.RoleClaim, principal).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (provider.EffectiveAdminValues.Any(values.Contains)) return AuthRoles.Admin;
        if (provider.EffectiveViewerValues.Any(values.Contains)) return AuthRoles.Viewer;
        return provider.DefaultRole;
    }

    /// <summary>The values of a claim, or of a dotted path inside a JSON claim (<c>realm_access.roles</c>).</summary>
    public static IEnumerable<string> RoleValues(string claimPath, ClaimsPrincipal principal)
    {
        var segments = claimPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) yield break;

        foreach (var claim in principal.FindAll(segments[0]))
        {
            if (segments.Length == 1 && !LooksLikeJson(claim.Value))
            {
                yield return claim.Value;
                continue;
            }

            JsonElement element;
            try { element = JsonDocument.Parse(claim.Value).RootElement; }
            catch (JsonException) { continue; }

            foreach (var segment in segments.Skip(1))
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                {
                    element = default;
                    break;
                }
            }
            foreach (var value in Strings(element))
                yield return value;
        }
    }

    /// <summary>What the UI shows for the user: preferred user name, name, e-mail or subject, first found.</summary>
    public static string DisplayName(ClaimsPrincipal principal) =>
        new[] { "preferred_username", "name", "email", "sub" }
            .Select(t => principal.FindFirst(t)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "unknown";

    private static bool LooksLikeJson(string value) => value.TrimStart() is ['[', ..] or ['{', ..];

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Array => element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!),
        _ => [],
    };
}
