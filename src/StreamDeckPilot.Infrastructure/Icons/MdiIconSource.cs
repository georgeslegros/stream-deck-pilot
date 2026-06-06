using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StreamDeckPilot.Infrastructure.Icons;

// Renders Material Design Icons glyphs from the embedded webfont onto a transparent
// 72x72 canvas, white, for tinting at compose time. Resolved by `builtin:<mdi-name>`.
internal static class MdiIconSource
{
    private static readonly Lazy<(FontFamily Family, IReadOnlyDictionary<string, int> Codepoints)?> Loaded = new(Load);

    public static bool IsAvailable => Loaded.Value is not null;

    public static bool Has(string name) =>
        Loaded.Value is { } v && v.Codepoints.ContainsKey(name);

    // Returns a transparent 72x72 image with the named glyph drawn at sizePixels, centred, white.
    public static Image<Rgba32>? Resolve(string name, int sizePixels)
    {
        if (Loaded.Value is not { } v) return null;
        if (!v.Codepoints.TryGetValue(name, out var cp)) return null;

        var glyph = char.ConvertFromUtf32(cp);
        var font = v.Family.CreateFont(sizePixels);
        var image = new Image<Rgba32>(72, 72);
        image.Mutate(ctx =>
        {
            var opts = new RichTextOptions(font)
            {
                Origin = new Vector2(36f, 36f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ctx.DrawText(opts, glyph, Color.White);
        });
        return image;
    }

    private static (FontFamily, IReadOnlyDictionary<string, int>)? Load()
    {
        try
        {
            var asm = typeof(MdiIconSource).Assembly;

            using var fontStream = OpenResource(asm, "materialdesignicons-webfont.ttf");
            using var metaStream = OpenResource(asm, "mdi-meta.json");
            if (fontStream is null || metaStream is null) return null;

            var collection = new FontCollection();
            var family = collection.Add(fontStream);

            using var doc = JsonDocument.Parse(metaStream);
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out var n) &&
                    entry.TryGetProperty("codepoint", out var c) &&
                    n.GetString() is { } name &&
                    c.GetString() is { } hex &&
                    int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                {
                    map[name] = cp;
                }
            }

            return (family, map);
        }
        catch
        {
            return null;
        }
    }

    private static Stream? OpenResource(Assembly asm, string suffix)
    {
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith(suffix, StringComparison.Ordinal));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }
}
