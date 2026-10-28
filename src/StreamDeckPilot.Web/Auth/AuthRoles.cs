using System.Security.Claims;

namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// The two roles (#97). <c>admin</c> changes config, presses keys through the mirror and manages
/// keys; <c>viewer</c> is read-only. The v0.9.0 pages need <see cref="ViewerPolicy"/>; editor pages
/// will need <see cref="AdminPolicy"/>.
/// </summary>
public static class AuthRoles
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";

    /// <summary>Any signed-in user with a role: admins can do everything a viewer can.</summary>
    public const string ViewerPolicy = "Viewer";
    public const string AdminPolicy = "Admin";

    /// <summary>How the user signed in: <c>local</c> or <c>oidc:&lt;provider&gt;</c>.</summary>
    public const string MethodClaim = "sdp:method";

    /// <summary>When the user signed in (Unix milliseconds); the session ends <c>Auth:MaxSessionDays</c> later.</summary>
    public const string SignedInClaim = "sdp:signed_in";

    /// <summary>A local account's security stamp at sign-in; the session ends when the account's stamp changes.</summary>
    public const string StampClaim = "sdp:stamp";

    /// <summary>The local account's name (local sign-ins only).</summary>
    public const string LocalUserClaim = "sdp:local_user";

    public static bool IsKnown(string role) => role is Admin or Viewer;

    public static bool HasAnyRole(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && (user.IsInRole(Admin) || user.IsInRole(Viewer));

    /// <summary>The principal the session cookie carries for a provider user.</summary>
    public static ClaimsPrincipal Principal(string id, string name, string role, string method, DateTimeOffset signedInAt) =>
        new(new ClaimsIdentity(Claims(id, name, role, method, signedInAt), authenticationType: method, ClaimTypes.Name, ClaimTypes.Role));

    /// <summary>The principal the session cookie carries for a local account, with its security stamp.</summary>
    public static ClaimsPrincipal Principal(LocalUser user, DateTimeOffset signedInAt) =>
        new(new ClaimsIdentity(
        [
            .. Claims($"local:{user.UserName}", user.UserName, user.Role, "local", signedInAt),
            new Claim(LocalUserClaim, user.UserName),
            new Claim(StampClaim, LocalUserStore.StampOf(user)),
        ], authenticationType: "local", ClaimTypes.Name, ClaimTypes.Role));

    private static Claim[] Claims(string id, string name, string role, string method, DateTimeOffset signedInAt) =>
    [
        new(ClaimTypes.NameIdentifier, id),
        new(ClaimTypes.Name, name),
        new(ClaimTypes.Role, role),
        new(MethodClaim, method),
        new(SignedInClaim, signedInAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];
}
