using System.Security.Cryptography;
using System.Text;
using StreamDeckPilot.Web;

namespace StreamDeckPilot.Api.Middleware;

public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
{
    public const string OpenApiPath = "/openapi.json";
    public const string LiveHubPath = Events.LiveHub.Path;

    private static readonly byte[] _invalid = Encoding.UTF8.GetBytes("__invalid__");

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

        // The web UI (only mapped when Web:Enabled=true) is exempt: pages, the Blazor circuit and
        // static assets carry WebUiEndpointMetadata. Routing runs before this middleware, so the
        // matched endpoint is known here. Until sign-in (#97) the UI is for private networks only.
        if (context.GetEndpoint()?.Metadata.GetMetadata<WebUiEndpointMetadata>() is not null)
        {
            await next(context);
            return;
        }

        var expected = config["ApiKey"] ?? string.Empty;
        var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? string.Empty;

        // The live hub (#108) also takes the key the way SignalR clients send a token: as a Bearer
        // header (AccessTokenProvider / accessTokenFactory). Never from the query string
        // (SignalR's access_token): URLs end up in host and proxy access logs, and the key with
        // them. Browsers can't send headers on a WebSocket, which is fine: the web UI runs
        // in-process, and other clients can send headers. The key is never logged here.
        if (provided.Length == 0 && context.Request.Path.StartsWithSegments(LiveHubPath))
        {
            var authorization = context.Request.Headers.Authorization.FirstOrDefault();
            if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                provided = authorization["Bearer ".Length..].Trim();
        }

        var expectedBytes = expected.Length > 0 ? Encoding.UTF8.GetBytes(expected) : _invalid;
        var providedBytes = Encoding.UTF8.GetBytes(provided.Length > 0 ? provided : "__missing__");

        // Constant-length comparison to resist timing attacks
        var match = expectedBytes.Length == providedBytes.Length
                    && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);

        if (!match)
        {
            // Same problem shape as every other error; `error` stays for clients that branch on it.
            await Results.Problem(
                    detail: "Missing or invalid X-Api-Key header.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["error"] = "invalid_api_key" })
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
