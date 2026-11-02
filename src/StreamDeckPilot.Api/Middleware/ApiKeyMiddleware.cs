using Microsoft.Extensions.Options;
using StreamDeckPilot.Api.Endpoints;
using StreamDeckPilot.Core.Security;
using StreamDeckPilot.Infrastructure.Security;
using StreamDeckPilot.Web;
using StreamDeckPilot.Web.Auth;

namespace StreamDeckPilot.Api.Middleware;

/// <summary>
/// Who may call the API: an API key (the bootstrap key or a named key, #98) in <c>X-Api-Key</c>, or a
/// signed-in web UI session (#97). Read-only keys and viewer sessions may only read. See docs/auth.md.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IApiKeyValidator validator, ApiKeyStore keys, TimeProvider time)
{
    public const string OpenApiPath = "/openapi.json";
    public const string LiveHubPath = Events.LiveHub.Path;
    public const string EventStreamPath = Events.EventStreamEndpoints.Path;

    /// <summary>The accepted key's (or session's) identity, for endpoints that need it.</summary>
    public const string IdentityItem = "sdp.apiKey";

    /// <summary>
    /// For a hub connection opened with a named key: cancelled when the key is revoked or expires
    /// (<see cref="Events.LiveHub"/> aborts the connection). Event streams use RequestAborted directly.
    /// </summary>
    public const string KeyEndedItem = "sdp.apiKeyEnded";

    /// <summary>
    /// The header a browser session must send with an unsafe API call. A page on another site can't
    /// add a custom header to a request without a CORS preflight, which this app never grants, so a
    /// forged form post or fetch can't carry it. The value isn't checked.
    /// </summary>
    public const string CsrfHeader = "X-Requested-With";

    public async Task InvokeAsync(HttpContext context)
    {
        // Health and the OpenAPI document are exempt. The document is mapped at the exact path
        // /openapi.json (Program.cs); a "/openapi" segment prefix never matched it (#80). It only
        // describes the public API surface, nothing configured or stored.
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.Equals(OpenApiPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // The web UI (only mapped when Web:Enabled=true) doesn't use the key: pages, the Blazor
        // circuit and static assets carry WebUiEndpointMetadata, and pages and the circuit need a
        // signed-in user instead (authorization middleware, #97). Routing runs before this
        // middleware, so the matched endpoint is known here.
        if (context.GetEndpoint()?.Metadata.GetMetadata<WebUiEndpointMetadata>() is not null)
        {
            await next(context);
            return;
        }

        var isHub = context.Request.Path.StartsWithSegments(LiveHubPath);
        var isLive = isHub || context.Request.Path.StartsWithSegments(EventStreamPath);

        // The live hub and event stream also take a signed-in UI session (#97), for browser
        // clients; the Origin check (OriginCheckMiddleware) already refused other sites. Both only read.
        if (isLive && context.User.HasAnyRole())
        {
            await next(context);
            return;
        }

        var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? string.Empty;

        // The live hub (#108) also takes the key the way SignalR clients send a token: as a Bearer
        // header (AccessTokenProvider / accessTokenFactory). Never from the query string
        // (SignalR's access_token): URLs end up in host and proxy access logs, and the key with
        // them. The key is never logged here.
        if (provided.Length == 0 && isHub)
        {
            var authorization = context.Request.Headers.Authorization.FirstOrDefault();
            if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                provided = authorization["Bearer ".Length..].Trim();
        }

        // A signed-in UI session may call the REST API too (#98): admins fully, viewers read-only.
        if (provided.Length == 0 && context.User.HasAnyRole())
        {
            await SessionAsync(context);
            return;
        }

        if (validator.Validate(provided) is not { } identity)
        {
            // Same problem shape as every other error; `error` stays for clients that branch on it.
            // One answer for every failure: missing, unknown, malformed, revoked or expired.
            await Results.Problem(
                    detail: "Missing or invalid X-Api-Key header.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["error"] = "invalid_api_key" })
                .ExecuteAsync(context);
            return;
        }

        // Read-only keys: safe methods only. The hub's methods (subscribe, snapshot) only read,
        // but SignalR's transports POST, so the hub is allowed whatever the method.
        if (identity.ReadOnly && !isHub && !IsSafe(context.Request.Method))
        {
            await ApiProblem.Forbidden("read_only_key", "This API key is read-only; it can't change anything.").ExecuteAsync(context);
            return;
        }

        context.Items[IdentityItem] = identity;

        // A named key's revocation or expiry also ends what it already opened: the event stream
        // (through RequestAborted, which the stream loop watches) and hub connections.
        if (isLive && identity.KeyId is { } keyId)
        {
            var ended = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, keys.RevocationToken(keyId));
            context.Response.RegisterForDispose(ended);
            if (identity.ExpiresAt is { } expiresAt)
            {
                // Timers can't wait longer than ~49 days: a connection that outlives MaxWait is ended
                // early and the client reconnects, which checks the key again.
                var wait = Math.Clamp((expiresAt - time.GetUtcNow()).Ticks, 0, MaxWait.Ticks);
                var expiry = new CancellationTokenSource(TimeSpan.FromTicks(wait), time);
                context.Response.RegisterForDispose(expiry);
                context.Response.RegisterForDispose(expiry.Token.Register(ended.Cancel));
            }
            context.RequestAborted = ended.Token;
            context.Items[KeyEndedItem] = ended.Token;
        }

        await next(context);
    }

    private async Task SessionAsync(HttpContext context)
    {
        var admin = context.User.IsInRole(AuthRoles.Admin);
        if (!IsSafe(context.Request.Method))
        {
            if (!admin)
            {
                await ApiProblem.Forbidden("read_only_session", "Viewers can't change anything; sign in as an admin.").ExecuteAsync(context);
                return;
            }
            // Cross-site request forgery: a browser sends the session cookie with any request to
            // this site. Require the custom header (no other site can add it) and, when the browser
            // says where the request comes from, that it's this app.
            if (string.IsNullOrEmpty(context.Request.Headers[CsrfHeader].FirstOrDefault()))
            {
                await ApiProblem.Forbidden("csrf_header_required",
                    $"A signed-in browser session must send the {CsrfHeader} header with changes, or use an API key.").ExecuteAsync(context);
                return;
            }
            if (context.Request.Headers.Origin.FirstOrDefault() is { Length: > 0 } origin &&
                !string.Equals(origin.TrimEnd('/'), ExpectedOrigin(context), StringComparison.OrdinalIgnoreCase))
            {
                await ApiProblem.Forbidden("cross_origin", "Changes from another site are refused.").ExecuteAsync(context);
                return;
            }
        }

        context.Items[IdentityItem] = new ApiKeyIdentity($"session:{context.User.Identity?.Name}", ReadOnly: !admin, IsBootstrap: false);
        await next(context);
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(24);

    private static string ExpectedOrigin(HttpContext context) =>
        context.RequestServices.GetService<IOptions<WebOptions>>()?.Value.NormalisedPublicOrigin
        ?? $"{context.Request.Scheme}://{context.Request.Host.Value}";
}
