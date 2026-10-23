using Microsoft.Net.Http.Headers;
using StreamDeckPilot.Infrastructure.Rendering;

namespace StreamDeckPilot.Api.Endpoints;

/// <summary>
/// Live preview (#27): PNG images of what a deck shows, rendered without touching the hardware.
/// Responses carry an ETag over the image bytes and <c>Cache-Control: no-cache</c>, so a client can
/// poll cheaply with <c>If-None-Match</c> and get a 304 while nothing changed.
/// </summary>
public static class PreviewEndpoints
{
    public const string SourceHeader = "X-Preview-Source";

    public static void MapPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/devices/{serial}/preview.png", async (string serial, string? page, int? scale,
                HttpContext http, DevicePreviewService preview) =>
            {
                if (ScaleError(scale) is { } bad) return bad;
                return Respond(http, await preview.RenderBoardAsync(serial, page, scale ?? 1));
            })
            .WithName("GetDevicePreview")
            .WithSummary("Image of a device's keys")
            .WithDescription("A PNG of the whole board: the page on screen by default, or `page`. For the page on screen of a connected deck it is exactly what was last sent to the keys; otherwise it is composed from desired state. `scale` is 1 (72 px keys) or 2. The `X-Preview-Source` header says `device` or `desired-state`.")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/devices/{serial}/keys/{index:int}.png", async (string serial, int index, string? page, int? scale,
                HttpContext http, DevicePreviewService preview) =>
            {
                if (ScaleError(scale) is { } bad) return bad;
                return Respond(http, await preview.RenderKeyAsync(serial, index, page, scale ?? 1));
            })
            .WithName("GetKeyPreview")
            .WithSummary("Image of one key")
            .WithDescription("A PNG of one key (0-based index, row by row), with the same sources and parameters as the board preview.")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static IResult? ScaleError(int? scale) =>
        scale is null or (>= 1 and <= DevicePreviewService.MaxScale)
            ? null
            : ApiProblem.BadRequest($"scale must be between 1 and {DevicePreviewService.MaxScale}.");

    private static IResult Respond(HttpContext http, PreviewResult result)
    {
        if (result.Status != PreviewStatus.Ok)
            return ApiProblem.NotFound(result.Error!);

        var image = result.Image!;
        var headers = http.Response.Headers;
        headers.ETag = image.ETag;
        headers.CacheControl = "no-cache";
        headers[SourceHeader] = image.Source == PreviewSource.Device ? "device" : "desired-state";

        var ifNoneMatch = http.Request.Headers.IfNoneMatch;
        if (ifNoneMatch.Count > 0 && EntityTagHeaderValue.TryParseList(ifNoneMatch, out var tags)
            && tags.Any(t => t.Tag == "*" || t.Compare(new EntityTagHeaderValue(image.ETag), useStrongComparison: false)))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        return Results.File(image.Png, "image/png");
    }
}
