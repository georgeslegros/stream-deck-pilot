using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// Ends sessions that should no longer be valid (#97):
/// <list type="bullet">
/// <item>every session after <see cref="AuthOptions.MaxSessionDays"/>, however active, so provider
/// users sign in again and a role or group removed at the provider takes effect;</item>
/// <item>a local account's sessions once the account is deleted, its password or role changes (its
/// security stamp), checked every <see cref="AuthOptions.RevalidateMinutes"/>.</item>
/// </list>
/// The cookie is checked on each request (<see cref="ValidateCookieAsync"/>); an open tab's circuit
/// by <see cref="SessionRevalidatingAuthenticationStateProvider"/>.
/// </summary>
public sealed class SessionValidator(LocalUserStore users, IOptions<AuthOptions> options, TimeProvider time, SignInAudit audit)
{
    private const string CheckedItem = "sdp.checked";

    /// <summary>Why the session must end, or null while it is valid.</summary>
    public async Task<string?> ProblemAsync(ClaimsPrincipal user, bool checkAccount, CancellationToken ct = default)
    {
        if (user.Identity?.IsAuthenticated != true) return null;

        if (!long.TryParse(user.FindFirst(AuthRoles.SignedInClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var signedInMs))
            return "session_invalid";
        if (time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(signedInMs) >= TimeSpan.FromDays(options.Value.MaxSessionDays))
            return "max_lifetime";

        if (!checkAccount || user.FindFirst(AuthRoles.LocalUserClaim)?.Value is not { } userName) return null;
        if (await users.StateAsync(ct) == LocalUsersState.Unreadable) return "accounts_unreadable";
        var account = await users.FindAsync(userName, ct);
        if (account is null) return "account_deleted";
        if (LocalUserStore.StampOf(account) != user.FindFirst(AuthRoles.StampClaim)?.Value) return "account_changed";
        if (!user.IsInRole(account.Role)) return "role_changed";
        return null;
    }

    /// <summary>Cookie hook: the lifetime on every request, the account once per revalidation interval.</summary>
    public async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is not { } principal) return;
        var now = time.GetUtcNow();
        var due = !(context.Properties.Items.TryGetValue(CheckedItem, out var last)
                    && long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var lastMs)
                    && now - DateTimeOffset.FromUnixTimeMilliseconds(lastMs) < TimeSpan.FromMinutes(options.Value.RevalidateMinutes));

        if (await ProblemAsync(principal, checkAccount: due, context.HttpContext.RequestAborted) is { } problem)
        {
            audit.SessionEnded(principal.Identity?.Name, problem, context.HttpContext.Connection.RemoteIpAddress);
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        if (due)
        {
            context.Properties.Items[CheckedItem] = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            context.ShouldRenew = true;
        }
    }
}

/// <summary>
/// An open tab's circuit keeps the user it started with; this re-checks it every
/// <see cref="AuthOptions.RevalidateMinutes"/> with <see cref="SessionValidator"/>. When the session
/// has ended, the circuit's user becomes anonymous and the page sends the browser to sign-in.
/// This is a security check on a timer, not a UI refresh (#108's no-polling rule is about data).
/// </summary>
public sealed class SessionRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggers, SessionValidator validator, IOptions<AuthOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggers)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(options.Value.RevalidateMinutes);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken) =>
        await validator.ProblemAsync(state.User, checkAccount: true, cancellationToken) is null;
}
