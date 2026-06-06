using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StreamDeckPilot.Infrastructure.Icons;

public static class GeneratedIconSource
{
    // Text tokens drawn white-on-transparent; tinted to the tile ink at compose time.
    private static readonly Dictionary<string, string> Definitions = new()
    {
        ["thermometer"]  = "T°",
        ["humidity"]     = "H%",
        ["co2"]          = "CO₂",
        ["power"]        = "⚡",
        ["home"]         = "⌂",
        ["arrow-left"]   = "◄",
        ["arrow-right"]  = "►",
        ["fallback"]     = "?",
        ["placeholder"]  = "…",
    };

    private static readonly Dictionary<string, byte[]> Cache = new();
    private static readonly Font? IconFont = TryLoadFont(18f);

    internal static Font? TryLoadFont(float size, FontStyle style = FontStyle.Regular)
    {
        foreach (var name in new[] { "DejaVu Sans", "Liberation Sans", "Arial", "FreeSans", "Noto Sans" })
            if (SystemFonts.TryGet(name, out var family))
                return family.CreateFont(size, style);
        return SystemFonts.Families.FirstOrDefault() is { } any ? any.CreateFont(size, style) : null;
    }

    public static byte[]? Load(string name)
    {
        if (!Definitions.TryGetValue(name, out var label)) return null;
        if (Cache.TryGetValue(name, out var cached)) return cached;
        var bytes = Generate(label);
        Cache[name] = bytes;
        return bytes;
    }

    public static IEnumerable<string> KnownNames => Definitions.Keys;

    private static byte[] Generate(string label)
    {
        // Transparent canvas: glyph only, in white so the composer can tint to the tile ink.
        using var image = new Image<Rgba32>(72, 72);
        image.Mutate(ctx =>
        {
            if (IconFont is not null)
            {
                var opts = new RichTextOptions(IconFont)
                {
                    Origin = new Vector2(36f, 36f),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ctx.DrawText(opts, label, Color.White);
            }
        });
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }
}
