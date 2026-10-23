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
        // header (negotiate, long polling, SSE) or an access_token query parameter (WebSockets, which
        // browsers can't give headers). Only there, so the key isn't invited into other URLs.
        if (provided.Length == 0 && context.Request.Path.StartsWithSegments(LiveHubPath))
        {
            var authorization = context.Request.Headers.Authorization.FirstOrDefault();
            provided = authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization["Bearer ".Length..].Trim()
                : context.Request.Query["access_token"].FirstOrDefault() ?? string.Empty;
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
