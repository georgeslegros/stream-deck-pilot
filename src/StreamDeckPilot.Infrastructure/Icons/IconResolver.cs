using System.Collections.Concurrent;
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
            return ResolveCustom(reference, serial, reference[7..], sizePixels)?.Clone() ?? LoadPng(Fallback);

        return LoadPng(Fallback);
    }

    /// <summary>Drops any cached decode of the file, e.g. after it was uploaded again or deleted.</summary>
    public void Invalidate(string serial, string filename)
    {
        foreach (var key in _customCache.Keys)
            if (key.Serial == serial && key.File == filename)
                _customCache.TryRemove(key, out _);
    }

    // Custom icons used to be read and fully decoded on every render of the key (#77). The decoded,
    // downscaled image is now cached per file version (last write time and length): a re-render
    // costs one stat and a clone of a key-sized image. A failed or refused load is cached too, so
    // its warning is logged once per file version. Cached images are shared and never mutated:
    // callers get a clone. Replaced entries aren't disposed, because another render may be
    // cloning them; the GC reclaims them.
    private readonly ConcurrentDictionary<(string Serial, string File, int Size), CachedIcon> _customCache = new();

    private sealed record CachedIcon(DateTime LastWriteUtc, long Length, Image<Rgba32>? Image);

    private Image<Rgba32>? ResolveCustom(string reference, string serial, string filename, int sizePixels)
    {
        var info = customSource.Stat(serial, filename);
        if (info is null)
        {
            logger.LogWarning("Custom icon '{Reference}' not found for device {Serial}, using fallback", reference, serial);
            return null;
        }

        var key = (serial, filename, sizePixels);
        if (_customCache.TryGetValue(key, out var cached)
            && cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Image;

        var image = LoadCustom(reference, serial, filename, sizePixels);
        _customCache[key] = new CachedIcon(info.LastWriteTimeUtc, info.Length, image);
        return image;
    }

    private Image<Rgba32>? LoadCustom(string reference, string serial, string filename, int sizePixels)
    {
        var bytes = customSource.Load(serial, filename);
        if (bytes is null)
        {
            logger.LogWarning("Custom icon '{Reference}' not found for device {Serial}, using fallback", reference, serial);
            return null;
        }

        // Read the dimensions from the header before decoding: a tiny file can declare a canvas
        // that takes gigabytes to decode. Covers files put on the volume by hand, too.
        var size = IconImageFormats.Identify(bytes);
        if (size is null)
        {
            logger.LogWarning("Custom icon '{Reference}' for device {Serial} is not a valid PNG or JPEG, using fallback", reference, serial);
            return null;
        }
        if (!IconImageFormats.IsWithinLimits(size.Value))
        {
            logger.LogWarning(
                "Custom icon '{Reference}' for device {Serial} is {Width}x{Height}, over the {Max}x{Max} limit, using fallback",
                reference, serial, size.Value.Width, size.Value.Height, IconImageFormats.MaxDimension, IconImageFormats.MaxDimension);
            return null;
        }

        var image = LoadPng(bytes);
        if (image is null)
        {
            logger.LogWarning("Custom icon '{Reference}' for device {Serial} is not a valid PNG or JPEG, using fallback", reference, serial);
            return null;
        }

        // Shrink to the smallest image that still covers the requested square, so the cached copy
        // and each clone stay key-sized. The composer's own crop-resize then gives the same framing.
        if (Math.Min(image.Width, image.Height) > sizePixels)
            image.Mutate(i => i.Resize(new ResizeOptions
            {
                Size = new Size(sizePixels, sizePixels),
                Mode = ResizeMode.Min,
                Sampler = KnownResamplers.Lanczos3,
            }));
        return image;
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
