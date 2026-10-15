using Microsoft.AspNetCore.Http.Features;
using StreamDeckPilot.Core.Validation;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Persistence;

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
        app.MapGet("/devices/{serial}/images", async (string serial, CatalogueStore catalogue,
            CustomImageSource store) =>
        {
            if (!await IsKnownAsync(catalogue, serial)) return UnknownDevice(serial);
            return Results.Ok(store.List(serial).Select(f => new { filename = f, @ref = $"custom:{f}" }));
        });

        app.MapPost("/devices/{serial}/images", async (string serial, HttpRequest request,
            CatalogueStore catalogue, CustomImageSource store, IconResolver resolver) =>
        {
            if (request.ContentLength > MaxRequestBytes)
                return TooLarge();
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxRequestBytes;

            // Checked first, so an upload for an unknown serial never creates images/<serial>/.
            if (!await IsKnownAsync(catalogue, serial)) return UnknownDevice(serial);

            if (!request.HasFormContentType)
                return ApiProblem.BadRequest("Expected multipart/form-data.");

            IFormCollection form;
            try { form = await request.ReadFormAsync(); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return TooLarge();
            }
            var file = form.Files.FirstOrDefault();
            if (file is null)
                return ApiProblem.BadRequest("No file uploaded.");

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return ApiProblem.BadRequest("Only PNG and JPEG files are accepted.");

            if (file.Length > MaxBytes)
                return ApiProblem.BadRequest("File exceeds the 512 KB limit.");

            // Prevent path traversal: the same rule as custom: references in the config.
            var safeName = file.FileName;
            if (!IconReference.IsBareFileName(safeName) || !IconReference.IsBareFileName(serial))
                return ApiProblem.BadRequest("Invalid filename.");

            // The extension is only a hint: check the content itself is PNG or JPEG.
            using var content = new MemoryStream((int)file.Length);
            await using (var upload = file.OpenReadStream())
                await upload.CopyToAsync(content);
            if (!IconImageFormats.IsSupported(content.GetBuffer().AsSpan(0, (int)content.Length)))
                return ApiProblem.BadRequest("File content is not a PNG or JPEG image.");

            // A small, highly compressed file can declare a huge canvas: check the header's
            // dimensions without decoding the pixels.
            content.Position = 0;
            var size = IconImageFormats.Identify(content);
            if (size is null)
                return ApiProblem.BadRequest("File content is not a PNG or JPEG image.");
            if (!IconImageFormats.IsWithinLimits(size.Value))
                return ApiProblem.BadRequest(
                    $"Image is {size.Value.Width}x{size.Value.Height}; the limit is " +
                    $"{IconImageFormats.MaxDimension}x{IconImageFormats.MaxDimension} pixels.");

            content.Position = 0;
            var @ref = await store.SaveAsync(serial, safeName, content);
            resolver.Invalidate(serial, safeName);
            return Results.Ok(new { @ref });
        });

        app.MapDelete("/devices/{serial}/images/{filename}", async (string serial, string filename,
            CatalogueStore catalogue, CustomImageSource store, IconResolver resolver) =>
        {
            if (!await IsKnownAsync(catalogue, serial)) return UnknownDevice(serial);

            if (!IconReference.IsBareFileName(filename) || !IconReference.IsBareFileName(serial))
                return ApiProblem.BadRequest("Invalid filename.");

            if (!store.Delete(serial, filename))
                return ApiProblem.NotFound($"No image '{filename}' for device '{serial}'.");
            resolver.Invalidate(serial, filename);
            return Results.NoContent();
        });
    }

    private static async Task<bool> IsKnownAsync(CatalogueStore catalogue, string serial) =>
        (await catalogue.LoadAsync()).Devices.Any(d => d.Serial == serial);

    private static IResult UnknownDevice(string serial) =>
        ApiProblem.NotFound($"Device '{serial}' is not in the catalogue.");

    private static IResult TooLarge() =>
        ApiProblem.TooLarge($"Request exceeds the {MaxRequestBytes / 1024} KB upload limit.");
}
