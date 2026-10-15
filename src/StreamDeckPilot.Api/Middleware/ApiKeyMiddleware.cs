using System.Security.Cryptography;
using System.Text;

namespace StreamDeckPilot.Api.Middleware;

public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
{
    public const string OpenApiPath = "/openapi.json";

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

        var expected = config["ApiKey"] ?? string.Empty;
        var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? string.Empty;

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
