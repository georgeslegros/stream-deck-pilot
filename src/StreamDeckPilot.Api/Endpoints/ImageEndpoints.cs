using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Api.Endpoints;

public static class ImageEndpoints
{
    private static readonly HashSet<string> AllowedExtensions = [".png", ".jpg", ".jpeg"];
    private const long MaxBytes = 512 * 1024;

    public static void MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/devices/{serial}/images", async (string serial, CatalogueStore catalogue,
            CustomImageSource store) =>
        {
            if (!await IsKnownAsync(catalogue, serial)) return UnknownDevice(serial);
            return Results.Ok(store.List(serial).Select(f => new { filename = f, @ref = $"custom:{f}" }));
        });

        app.MapPost("/devices/{serial}/images", async (string serial, HttpRequest request,
            CatalogueStore catalogue, CustomImageSource store) =>
        {
            // Checked first, so an upload for an unknown serial never creates images/<serial>/.
            if (!await IsKnownAsync(catalogue, serial)) return UnknownDevice(serial);

            if (!request.HasFormContentType)
                return ApiProblem.BadRequest("Expected multipart/form-data.");

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null)
                return ApiProblem.BadRequest("No file uploaded.");

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return ApiProblem.BadRequest("Only PNG and JPEG files are accepted.");

            if (file.Length > MaxBytes)
                return ApiProblem.BadRequest("File exceeds the 512 KB limit.");

            // Prevent path traversal
            var safeName = Path.GetFileName(file.FileName);
            if (safeName != file.FileName || safeName.Contains(".."))
                return ApiProblem.BadRequest("Invalid filename.");

            // The extension is only a hint: check the content itself is PNG or JPEG.
            using var content = new MemoryStream((int)file.Length);
            await using (var upload = file.OpenReadStream())
                await upload.CopyToAsync(content);
            if (!IconImageFormats.IsSupported(content.GetBuffer().AsSpan(0, (int)content.Length)))
                return ApiProblem.BadRequest("File content is not a PNG or JPEG image.");

            content.Position = 0;
            var @ref = await store.SaveAsync(serial, safeName, content);
            return Results.Ok(new { @ref });
        });

        app.MapDelete("/devices/{serial}/images/{filename}", async (string serial, string filename,
            CatalogueStore catalogue, CustomImageSource store) =>
        {
            if (!await IsKnownAsync(catalogue, serial)) return UnknownDevice(serial);

            if (filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
                return ApiProblem.BadRequest("Invalid filename.");

            return store.Delete(serial, filename)
                ? Results.NoContent()
                : ApiProblem.NotFound($"No image '{filename}' for device '{serial}'.");
        });
    }

    private static async Task<bool> IsKnownAsync(CatalogueStore catalogue, string serial) =>
        (await catalogue.LoadAsync()).Devices.Any(d => d.Serial == serial);

    private static IResult UnknownDevice(string serial) =>
        ApiProblem.NotFound($"Device '{serial}' is not in the catalogue.");
}
