using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StreamDeckPilot.Infrastructure.Icons;

public sealed class IconResolver(CustomImageSource customSource, ILogger<IconResolver> logger)
{
    private static readonly byte[] Fallback =
        GeneratedIconSource.Load("fallback") ?? GenerateFallback();

    // Raw PNG bytes (legacy API; still used by API/preview paths and tests).
    public byte[] Resolve(string? reference, string serial)
    {
        if (string.IsNullOrEmpty(reference))
            return Fallback;

        if (reference.StartsWith("builtin:", StringComparison.Ordinal))
        {
            var name = reference[8..];
            return GeneratedIconSource.Load(name) ?? Fallback;
        }

        if (reference.StartsWith("custom:", StringComparison.Ordinal))
        {
            var filename = reference[7..];
            var bytes = customSource.Load(serial, filename);
            if (bytes is null)
            {
                logger.LogWarning("Custom icon '{Reference}' not found for device {Serial}, using fallback", reference, serial);
                return Fallback;
            }
            return bytes;
        }

        return Fallback;
    }

    // Transparent glyph image for compose-time tinting/sizing. Prefers the full MDI
    // font library for `builtin:<name>`, falling back to the generated text tokens.
    // Returns null only when nothing can be produced.
    public Image<Rgba32>? ResolveImage(string? reference, string serial, int sizePixels)
    {
        if (string.IsNullOrEmpty(reference))
            return LoadPng(Fallback);

        if (reference.StartsWith("builtin:", StringComparison.Ordinal))
        {
            var name = reference[8..];
            if (MdiIconSource.Resolve(name, sizePixels) is { } mdi)
                return mdi;
            var generated = GeneratedIconSource.Load(name);
            return generated is not null ? LoadPng(generated) : LoadPng(Fallback);
        }

        if (reference.StartsWith("custom:", StringComparison.Ordinal))
        {
            var filename = reference[7..];
            var bytes = customSource.Load(serial, filename);
            if (bytes is null)
            {
                logger.LogWarning("Custom icon '{Reference}' not found for device {Serial}, using fallback", reference, serial);
                return LoadPng(Fallback);
            }
            if (LoadPng(bytes) is { } custom)
                return custom;
            logger.LogWarning("Custom icon '{Reference}' for device {Serial} is not a valid PNG or JPEG, using fallback", reference, serial);
            return LoadPng(Fallback);
        }

        return LoadPng(Fallback);
    }

    // Decodes PNG or JPEG only; anything else (TIFF, BMP, GIF, WebP...) yields null.
    private static Image<Rgba32>? LoadPng(byte[] bytes)
    {
        try { return Image.Load<Rgba32>(IconImageFormats.Configuration, bytes); }
        catch { return null; }
    }

    private static byte[] GenerateFallback()
    {
        using var image = new Image<Rgba32>(72, 72);
        image.Mutate(ctx => ctx.Fill(new Color(new Rgba32(60, 60, 60))));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }
}
