using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// Sign-in for the web UI (#97, docs/auth.md): a cookie session, created by an OpenID Connect
/// provider or a local account. Wired only when the UI is on; then every page needs a signed-in user.
/// </summary>
public static class AuthExtensions
{
    public const string SignInPath = "/account/signin";
    public const string SetupPath = "/account/setup";
    public const string DeniedPath = "/account/denied";
    public const string SignOutPath = "/account/signout";
    public const string CookieName = "sdp.session";

    /// <summary>The Blazor circuit; the live endpoints are added by the host (they live in the Api).</summary>
    public static readonly PathString CircuitPath = "/_blazor";

    /// <summary>The sign-in, setup and sign-out forms, which the Origin check also covers.</summary>
    public static readonly PathString AccountPath = "/account";

    internal static IServiceCollection AddWebAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var web = configuration.GetSection(WebOptions.Section).Get<WebOptions>() ?? new();
        var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new();
        if ((web.Error() ?? auth.Error(web.NormalisedPublicOrigin)) is { } error)
            throw new InvalidOperationException(error);

        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.AddSingleton<LocalUserStore>();
        services.AddSingleton<SignInThrottle>();
        services.AddSingleton<SignInAudit>();
        services.AddSingleton<SessionValidator>();
        services.AddHostedService<FirstRunAnnouncer>();
        services.AddCascadingAuthenticationState();
        // Before AddRazorComponents, whose default provider is only added when none is registered.
        services.AddScoped<AuthenticationStateProvider, SessionRevalidatingAuthenticationStateProvider>();

        var builder = services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                // Lax, not Strict: the session must be sent when a provider redirects back to the app.
                o.Cookie.SameSite = SameSiteMode.Lax;
                // Secure whenever the request is HTTPS, which behind a TLS-terminating proxy comes
                // from X-Forwarded-Proto (honoured from trusted proxies only).
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(auth.SessionHours);
                o.SlidingExpiration = true;
                o.LoginPath = SignInPath;
                o.LogoutPath = SignOutPath;
                o.AccessDeniedPath = DeniedPath;
                o.ReturnUrlParameter = "returnUrl";
                // Pages redirect to sign-in; the circuit, hub, API and assets answer 401/403 instead.
                o.Events.OnRedirectToLogin = ctx => RedirectOrStatus(ctx, StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = ctx => RedirectOrStatus(ctx, StatusCodes.Status403Forbidden);
                // Maximum lifetime, and deleted or changed local accounts (SessionValidator).
                o.Events.OnValidatePrincipal = ctx => ctx.HttpContext.RequestServices.GetRequiredService<SessionValidator>().ValidateCookieAsync(ctx);
            });

        foreach (var provider in auth.Providers)
            builder.AddOpenIdConnect(provider.Scheme, provider.Label, o => ConfigureOidc(o, provider, web.NormalisedPublicOrigin!));

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthRoles.ViewerPolicy, p => p.RequireRole(AuthRoles.Admin, AuthRoles.Viewer))
            .AddPolicy(AuthRoles.AdminPolicy, p => p.RequireRole(AuthRoles.Admin));
        return services;
    }

    /// <summary>
    /// Authentication, the cross-site WebSocket guard and authorization. Call after the reverse-proxy
    /// middleware (so scheme, host and client address are resolved) and before the API key check.
    /// </summary>
    public static WebApplication UseWebAuth(this WebApplication app, params PathString[] livePaths)
    {
        if (!app.Configuration.IsWebUiEnabled()) return app;
        app.UseAuthentication();
        app.UseMiddleware<OriginCheckMiddleware>((IReadOnlyList<PathString>)[CircuitPath, AccountPath, .. livePaths]);
        app.UseAuthorization();
        return app;
    }

    /// <summary>Sign-out (a form post with an anti-forgery token) and the provider sign-in redirect.</summary>
    internal static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account").AllowAnonymous().WithMetadata(WebUiEndpointMetadata.Instance);

        // [FromForm] makes the endpoint validate the anti-forgery token (UseAntiforgery).
        account.MapPost("/signout", async (HttpContext context, SignInAudit audit, [FromForm] string? returnUrl) =>
        {
            var user = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name : null;
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (user is not null) audit.SignedOut(user, context.Connection.RemoteIpAddress);
            return Results.Redirect($"{context.Request.PathBase}{SignInPath}");
        });

        account.MapGet("/signin/oidc/{name}", (string name, string? returnUrl, HttpContext context, IOptions<AuthOptions> options) =>
        {
            var provider = options.Value.Providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return Results.NotFound();
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl, context.Request.PathBase) },
                [provider.Scheme]);
        });
    }

    /// <summary>
    /// Where to go after signing in: a path inside the app (under its path base), or the app's root.
    /// Anything else (another host, <c>//evil</c>, another app on the host) is ignored.
    /// </summary>
    public static string SafeReturnUrl(string? returnUrl, PathString pathBase)
    {
        var root = $"{pathBase}/";
        if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') ||
            returnUrl.StartsWith("//") || returnUrl.StartsWith("/\\") || returnUrl.Contains('\\'))
            return root;
        if (pathBase.HasValue && !PathString.FromUriComponent(returnUrl.Split('?', '#')[0]).StartsWithSegments(pathBase))
            return root;
        return returnUrl;
    }

    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        var request = context.Request;
        var isPage = HttpMethods.IsGet(request.Method)
                     && request.Headers.Accept.Any(a => a?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
                     && !request.Path.StartsWithSegments(CircuitPath);
        if (isPage) context.Response.Redirect(context.RedirectUri);
        else context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    private static void ConfigureOidc(OpenIdConnectOptions o, OidcProviderOptions provider, string publicOrigin)
    {
        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.Authority = provider.Authority;
        o.ClientId = provider.ClientId;
        o.ClientSecret = provider.ClientSecret;
        o.RequireHttpsMetadata = provider.RequireHttpsMetadata;
        // Authorization code with PKCE. The code comes back in the query string, so the Lax
        // correlation and nonce cookies are sent with it (form_post would need SameSite=None).
        o.ResponseType = "code";
        o.ResponseMode = "query";
        o.UsePkce = true;
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.Scope.Clear();
        foreach (var scope in provider.EffectiveScopes) o.Scope.Add(scope);
        o.CallbackPath = provider.CallbackPath;
        o.SignedOutCallbackPath = $"/signout-callback-oidc-{provider.Name}";
        o.RemoteSignOutPath = $"/signout-oidc-{provider.Name}";
        o.SaveTokens = false;
        o.MapInboundClaims = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        // Keep the role claim (and the user name) when they only come from the user-info endpoint.
        var roleRoot = provider.RoleClaim.Split('.')[0];
        o.ClaimActions.MapJsonKey(roleRoot, roleRoot);
        o.ClaimActions.MapJsonKey("preferred_username", "preferred_username");

        o.Events.OnRedirectToIdentityProvider = context =>
        {
            // Never from the Host or X-Forwarded-Host header: a spoofed host would send the code elsewhere.
            context.ProtocolMessage.RedirectUri = $"{publicOrigin}{context.Request.PathBase}{provider.CallbackPath}";
            return Task.CompletedTask;
        };

        o.Events.OnTicketReceived = context =>
        {
            var audit = context.HttpContext.RequestServices.GetRequiredService<SignInAudit>();
            var address = context.HttpContext.Connection.RemoteIpAddress;
            var method = $"oidc:{provider.Name}";
            var role = OidcClaims.Role(provider, context.Principal!);
            if (role is null)
            {
                audit.Failed(method, "no_role", address);
                context.Response.Redirect($"{context.Request.PathBase}{SignInPath}?error=no_role");
                context.HandleResponse();
                return Task.CompletedTask;
            }

            var name = OidcClaims.DisplayName(context.Principal!);
            var subject = context.Principal!.FindFirst("sub")?.Value ?? name;
            var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            context.Principal = AuthRoles.Principal($"{method}:{subject}", name, role, method, now);
            context.Properties!.IsPersistent = true;
            audit.Succeeded(method, name, role, address);
            return Task.CompletedTask;
        };

        o.Events.OnRemoteFailure = context =>
        {
            var audit = context.HttpContext.RequestServices.GetRequiredService<SignInAudit>();
            audit.Failed($"oidc:{provider.Name}", "remote_failure", context.HttpContext.Connection.RemoteIpAddress, context.Failure?.Message);
            context.Response.Redirect($"{context.Request.PathBase}{SignInPath}?error=remote_failure");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }
}
