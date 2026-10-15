using Microsoft.AspNetCore.Http.Features;
using StreamDeckPilot.Infrastructure.Icons;

namespace StreamDeckPilot.Api.Endpoints;

public static class ImageEndpoints
{
    private static readonly HashSet<string> AllowedExtensions = [".png", ".jpg", ".jpeg"];
    private const long MaxBytes = 512 * 1024;

    // The whole multipart body: the file plus generous room for headers and boundaries. Checked
    // before the form is read, so an oversized upload is refused instead of buffered (#77).
    internal const long MaxRequestBytes = MaxBytes + 64 * 1024;

    public static void MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/devices/{serial}/images", (string serial, CustomImageSource store) =>
            Results.Ok(store.List(serial).Select(f => new { filename = f, @ref = $"custom:{f}" })));

        app.MapPost("/devices/{serial}/images", async (string serial, HttpRequest request,
            CustomImageSource store, IconResolver resolver) =>
        {
            if (request.ContentLength > MaxRequestBytes)
                return TooLarge();
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxRequestBytes;

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data" });

            IFormCollection form;
            try { form = await request.ReadFormAsync(); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return TooLarge();
            }
            var file = form.Files.FirstOrDefault();
            if (file is null)
                return Results.BadRequest(new { error = "No file uploaded" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return Results.BadRequest(new { error = "Only PNG and JPEG files are accepted" });

            if (file.Length > MaxBytes)
                return Results.BadRequest(new { error = $"File exceeds 512 KB limit" });

            // Prevent path traversal
            var safeName = Path.GetFileName(file.FileName);
            if (safeName != file.FileName || safeName.Contains(".."))
                return Results.BadRequest(new { error = "Invalid filename" });

            // The extension is only a hint: check the content itself is PNG or JPEG.
            using var content = new MemoryStream((int)file.Length);
            await using (var upload = file.OpenReadStream())
                await upload.CopyToAsync(content);
            if (!IconImageFormats.IsSupported(content.GetBuffer().AsSpan(0, (int)content.Length)))
                return Results.BadRequest(new { error = "File content is not a PNG or JPEG image" });

            // A small, highly compressed file can declare a huge canvas: check the header's
            // dimensions without decoding the pixels.
            content.Position = 0;
            var size = IconImageFormats.Identify(content);
            if (size is null)
                return Results.BadRequest(new { error = "File content is not a PNG or JPEG image" });
            if (!IconImageFormats.IsWithinLimits(size.Value))
                return Results.BadRequest(new
                {
                    error = $"Image is {size.Value.Width}x{size.Value.Height}; the limit is " +
                            $"{IconImageFormats.MaxDimension}x{IconImageFormats.MaxDimension} pixels",
                });

            content.Position = 0;
            var @ref = await store.SaveAsync(serial, safeName, content);
            resolver.Invalidate(serial, safeName);
            return Results.Ok(new { @ref });
        });

        app.MapDelete("/devices/{serial}/images/{filename}", (string serial, string filename,
            CustomImageSource store, IconResolver resolver) =>
        {
            if (filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid filename" });

            if (!store.Delete(serial, filename))
                return Results.NotFound();
            resolver.Invalidate(serial, filename);
            return Results.NoContent();
        });
    }

    private static IResult TooLarge() =>
        Results.Json(new { error = $"Request exceeds the {MaxRequestBytes / 1024} KB upload limit" },
            statusCode: StatusCodes.Status413PayloadTooLarge);
}
