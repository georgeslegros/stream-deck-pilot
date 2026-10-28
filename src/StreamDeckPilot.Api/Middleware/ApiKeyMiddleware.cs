using StreamDeckPilot.Core.Security;
using StreamDeckPilot.Web;
using StreamDeckPilot.Web.Auth;

namespace StreamDeckPilot.Api.Middleware;

public sealed class ApiKeyMiddleware(RequestDelegate next, IApiKeyValidator validator)
{
    public const string OpenApiPath = "/openapi.json";
    public const string LiveHubPath = Events.LiveHub.Path;
    public const string EventStreamPath = Events.EventStreamEndpoints.Path;

    /// <summary>The accepted key's identity, for endpoints that need it (#98).</summary>
    public const string IdentityItem = "sdp.apiKey";

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

        // The live hub and event stream also take a signed-in UI session (#97), for browser
        // clients; the Origin check (OriginCheckMiddleware) already refused other sites.
        if ((context.Request.Path.StartsWithSegments(LiveHubPath) || context.Request.Path.StartsWithSegments(EventStreamPath))
            && context.User.HasAnyRole())
        {
            await next(context);
            return;
        }

        var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? string.Empty;

        // The live hub (#108) also takes the key the way SignalR clients send a token: as a Bearer
        // header (AccessTokenProvider / accessTokenFactory). Never from the query string
        // (SignalR's access_token): URLs end up in host and proxy access logs, and the key with
        // them. The key is never logged here.
        if (provided.Length == 0 && context.Request.Path.StartsWithSegments(LiveHubPath))
        {
            var authorization = context.Request.Headers.Authorization.FirstOrDefault();
            if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                provided = authorization["Bearer ".Length..].Trim();
        }

        if (validator.Validate(provided) is not { } identity)
        {
            // Same problem shape as every other error; `error` stays for clients that branch on it.
            await Results.Problem(
                    detail: "Missing or invalid X-Api-Key header.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["error"] = "invalid_api_key" })
                .ExecuteAsync(context);
            return;
        }

        context.Items[IdentityItem] = identity;
        await next(context);
    }
}
