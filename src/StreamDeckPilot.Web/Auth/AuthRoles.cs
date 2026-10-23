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

    public static bool IsKnown(string role) => role is Admin or Viewer;

    public static bool HasAnyRole(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && (user.IsInRole(Admin) || user.IsInRole(Viewer));

    /// <summary>The principal the session cookie carries, whatever the sign-in method.</summary>
    public static ClaimsPrincipal Principal(string id, string name, string role, string method) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role),
            new Claim(MethodClaim, method),
        ], authenticationType: method, ClaimTypes.Name, ClaimTypes.Role));
}
