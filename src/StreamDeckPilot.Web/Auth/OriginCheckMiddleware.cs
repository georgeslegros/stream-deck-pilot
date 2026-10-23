using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// Cross-site WebSocket hijacking guard (#97). WebSockets (and SignalR negotiation) aren't covered by
/// CORS, and a browser sends the session cookie to them from any page on the same site. So on the
/// Blazor circuit and the live endpoints, a request whose <c>Origin</c> isn't the app's own is
/// refused with 403. The app's origin is <c>Web:PublicOrigin</c> when set, otherwise the request's
/// scheme and host as resolved through trusted proxies. Requests without an <c>Origin</c> header
/// (scripts, curl, the .NET SignalR client) aren't browsers acting for a user and pass.
/// </summary>
public sealed class OriginCheckMiddleware(
    RequestDelegate next, IOptions<WebOptions> web, ILogger<OriginCheckMiddleware> logger, IReadOnlyList<PathString> paths)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (paths.Any(p => context.Request.Path.StartsWithSegments(p))
            && context.Request.Headers.Origin.FirstOrDefault() is { Length: > 0 } origin
            && !string.Equals(origin.TrimEnd('/'), Expected(context), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Refused {Path} from origin {Origin}: the app's origin is {Expected}. Behind a proxy, set Web:PublicOrigin or trust the proxy (Proxy:TrustedProxies).",
                context.Request.Path.Value, origin, Expected(context));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }

    private string Expected(HttpContext context) =>
        web.Value.NormalisedPublicOrigin ?? $"{context.Request.Scheme}://{context.Request.Host.Value}";
}
