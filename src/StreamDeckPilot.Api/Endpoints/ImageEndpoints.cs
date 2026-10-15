using StreamDeckPilot.Core.Validation;
using StreamDeckPilot.Infrastructure.Icons;

namespace StreamDeckPilot.Api.Endpoints;

public static class ImageEndpoints
{
    private static readonly HashSet<string> AllowedExtensions = [".png", ".jpg", ".jpeg"];
    private const long MaxBytes = 512 * 1024;

    public static void MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/devices/{serial}/images", (string serial, CustomImageSource store) =>
            Results.Ok(store.List(serial).Select(f => new { filename = f, @ref = $"custom:{f}" })));

        app.MapPost("/devices/{serial}/images", async (string serial, HttpRequest request,
            CustomImageSource store) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data" });

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null)
                return Results.BadRequest(new { error = "No file uploaded" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return Results.BadRequest(new { error = "Only PNG and JPEG files are accepted" });

            if (file.Length > MaxBytes)
                return Results.BadRequest(new { error = $"File exceeds 512 KB limit" });

            // Prevent path traversal: the same rule as custom: references in the config.
            var safeName = file.FileName;
            if (!IconReference.IsBareFileName(safeName) || !IconReference.IsBareFileName(serial))
                return Results.BadRequest(new { error = "Invalid filename" });

            // The extension is only a hint: check the content itself is PNG or JPEG.
            using var content = new MemoryStream((int)file.Length);
            await using (var upload = file.OpenReadStream())
                await upload.CopyToAsync(content);
            if (!IconImageFormats.IsSupported(content.GetBuffer().AsSpan(0, (int)content.Length)))
                return Results.BadRequest(new { error = "File content is not a PNG or JPEG image" });

            content.Position = 0;
            var @ref = await store.SaveAsync(serial, safeName, content);
            return Results.Ok(new { @ref });
        });

        app.MapDelete("/devices/{serial}/images/{filename}", (string serial, string filename,
            CustomImageSource store) =>
        {
            if (!IconReference.IsBareFileName(filename) || !IconReference.IsBareFileName(serial))
                return Results.BadRequest(new { error = "Invalid filename" });

            return store.Delete(serial, filename)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }
}
