using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using OpenMacroBoard.SDK;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Icons;

namespace StreamDeckPilot.Infrastructure.Rendering;

public sealed class KeyBitmapComposer(IconResolver iconResolver, ILogger<KeyBitmapComposer>? logger = null)
{
    // Icon failures are logged once per (icon, error type) so a broken icon cannot flood the log
    // on every render; the key is still drawn without the icon.
    private readonly ConcurrentDictionary<string, byte> _loggedIconFailures = new(StringComparer.Ordinal);

    private const int Size = 72;

    // ── Corner-icon watermark (tunable) ──────────────────────────────────────────
    // IconPlacement.Corner draws a large, faint watermark centred on the tile, sitting UNDER the
    // hero value and caption. These consts are the only knobs — adjust freely.

    // Glyph diameter in px. ~15% larger than the 66px centre hero icon.
    private const int WatermarkSize = 76;
    // Compositing opacity (0..1). Low so the background colour reads through it.
    private const float WatermarkOpacity = 0.22f;
    // Centred on the tile (behind the text). Slightly larger than the tile, so a few px clip on
    // each edge — intentional. (For a right-edge half-icon instead, use `Size - WatermarkSize / 2`.)
    private const int WatermarkLeft = (Size - WatermarkSize) / 2;
    // Vertically centred; may be slightly negative (clips top/bottom) — intentional.
    private const int WatermarkTop = (Size - WatermarkSize) / 2;

    private static readonly Color DefaultBg = new(new Rgba32(55, 55, 55));
    private static readonly Color InkDark = new(new Rgba32(26, 26, 26));

    // Type ramp (px). The hero value starts at 25.5 px (22 px from five characters) and steps
    // down until it fits the tile, so long values such as "1234.56" are never clipped.
    private const float ValueFontSize = 25.5f;
    private const float ValueFontSizeLong = 22f;
    private const float MinValueFontSize = 9f;
    private const float MaxValueWidth = Size - 2;   // a 1 px margin on each side
    private static readonly ConcurrentDictionary<float, Font?> ValueFonts = new();
    private static readonly Font? UnitFont = GeneratedIconSource.TryLoadFont(12f);
    private static readonly Font? CaptionFont = GeneratedIconSource.TryLoadFont(9.5f);

    public KeyBitmap Compose(ButtonRenderState state, string serial)
    {
        using var image = ComposeImage(state, serial);
        return KeyBitmap.Create.FromImageSharpImage(image);
    }

    // Composes the 72x72 key image. Caller owns (and must dispose) the returned image.
    // Used by Compose (wrapped into a KeyBitmap) and by preview/golden-image tooling.
    public Image<Rgba32> ComposeImage(ButtonRenderState state, string serial)
    {
        var image = new Image<Rgba32>(Size, Size);

        var bg = ParseColour(state.BackgroundColour) ?? DefaultBg;
        image.Mutate(ctx => ctx.Fill(bg));

        // Stale and broker-offline tiles desaturate the background first so ink is computed on the
        // drained colour.
        if (state.IsDimmed || state.IsOffline)
        {
            Desaturate(image);
            bg = SampleBackground(image);
        }

        var ink = Ink(bg);
        var hasCenter = !string.IsNullOrEmpty(state.CenterText);
        var hasBottom = !string.IsNullOrEmpty(state.BottomText);

        // Icon: placement is the user's choice, never inferred from the data. Centre text,
        // when present, owns the centre — a Center-placed icon yields to it.
        if (state.IconReference is not null)
        {
            if (state.IconPlacement == IconPlacement.Corner)
                // Faint right-anchored watermark, drawn UNDER the hero text/caption below.
                DrawWatermark(image, state.IconReference, serial, ink);
            else if (state.IconPlacement == IconPlacement.Center && !hasCenter)
            {
                // ~1.5x larger, clamped to the 72px tile: a full 1.5x (84/60) would overflow the
                // tile / collide with the caption band. Vertically centred in the space available.
                var iconSize = hasBottom ? 50 : 66;
                var iconY = hasBottom ? 6 : (Size - iconSize) / 2;
                DrawIcon(image, state.IconReference, serial, ink, size: iconSize, x: 8, y: iconY, centred: true);
            }
        }

        if (hasCenter)
            DrawCenterValue(image, state.CenterText!, ink, hasBottom);

        if (hasBottom)
            DrawBottomCaption(image, state.BottomText!, ink);

        // Status marker, top-right: broker offline (#26) wins over stale, since an outage is the
        // reason values stop updating.
        if (state.IsOffline)
            DrawStatusMarker(image, OfflineMarkerIcon, ink);
        else if (state.IsDimmed)
            DrawStatusMarker(image, ClockMarkerIcon, ink);

        // Blink "off" half (#23): the whole key drops to a quarter of its brightness. The value stays
        // faintly readable, and the alternation with the normal look is what catches the eye.
        if (state.IsBlinkOffPhase)
            Darken(image, BlinkOffBrightness);

        return image;
    }

    // Large hero value, centred. The value is split from its unit (first space) so the
    // number reads big and the unit sits small beneath it.
    private void DrawCenterValue(Image<Rgba32> image, string centerText, Color ink, bool hasBottom)
    {
        var (head, tail) = SplitValueUnit(centerText);
        var heroBaseline = hasBottom ? 40f : 44f;   // lift the hero when a caption is present so the rows fit

        if (!string.IsNullOrEmpty(head) && FitValueFont(head) is { } font)
        {
            // 1px black shadow keeps the value legible near the luminance threshold.
            DrawCentredText(image, head, font, Color.Black, x: 36f, baseline: heroBaseline + 1);
            DrawCentredText(image, head, font, ink, x: 36f, baseline: heroBaseline);
        }

        if (!string.IsNullOrEmpty(tail) && UnitFont is not null)
            DrawCentredText(image, tail, UnitFont, WithAlpha(ink, 0.75f), x: 36f, baseline: heroBaseline + 15);
    }

    private static Font? FitValueFont(string text)
    {
        var size = text.Length >= 5 ? ValueFontSizeLong : ValueFontSize;
        for (; size > MinValueFontSize; size -= 1f)
        {
            var font = ValueFontAt(size);
            if (font is null || TextMeasurer.MeasureBounds(text, new TextOptions(font)).Width <= MaxValueWidth)
                return font;
        }
        return ValueFontAt(MinValueFontSize);
    }

    private static Font? ValueFontAt(float size) =>
        ValueFonts.GetOrAdd(size, s => GeneratedIconSource.TryLoadFont(s, FontStyle.Bold));

    // Small caption along the bottom, e.g. a room name.
    private static void DrawBottomCaption(Image<Rgba32> image, string text, Color ink)
    {
        if (CaptionFont is null) return;
        DrawCentredText(image, Truncate(text, 10), CaptionFont, WithAlpha(ink, 0.85f), x: 36f, baseline: 69f);
    }

    private void DrawIcon(Image<Rgba32> image, string? reference, string serial, Color ink,
        int size, int x, int y, bool centred)
    {
        if (reference is null) return;
        var icon = iconResolver.ResolveImage(reference, serial, size);
        if (icon is null) return;
        try
        {
            using (icon)
            {
                icon.Mutate(i => i.Resize(new ResizeOptions
                {
                    Size = new Size(size, size),
                    Sampler = KnownResamplers.Lanczos3,
                }));
                Tint(icon, ink);
                var px = centred ? (Size - size) / 2 : x;
                image.Mutate(ctx => ctx.DrawImage(icon, new Point(px, y), 1f));
            }
        }
        catch (Exception ex) { IconFailed(reference, ex); }
    }

    // Large, faint watermark for IconPlacement.Corner. The glyph is resized and tinted like a
    // normal icon, then composited at WatermarkOpacity at WatermarkLeft/Top (centred by default).
    // The glyph is larger than the tile, so it lies partly outside it, and ImageSharp's DrawImage
    // requires the overlay rectangle to lie within bounds — so we crop the resized glyph to the
    // visible region and composite that crop at the clamped on-tile origin. Drawn before the hero
    // text, so the text always sits on top.
    private void DrawWatermark(Image<Rgba32> image, string? reference, string serial, Color ink)
    {
        if (reference is null) return;
        var icon = iconResolver.ResolveImage(reference, serial, WatermarkSize);
        if (icon is null) return;
        try
        {
            using (icon)
            {
                icon.Mutate(i => i.Resize(new ResizeOptions
                {
                    Size = new Size(WatermarkSize, WatermarkSize),
                    Sampler = KnownResamplers.Lanczos3,
                }));
                Tint(icon, ink);

                // Intersect the glyph's target rectangle (WatermarkLeft, WatermarkTop, WxW) with the
                // tile (0,0,Size,Size). cropX/cropY are the source offset into the glyph; destX/destY
                // the on-tile origin; cropW/cropH the overlap size. All guaranteed in-bounds.
                var destX = Math.Max(0, WatermarkLeft);
                var destY = Math.Max(0, WatermarkTop);
                var cropX = destX - WatermarkLeft;
                var cropY = destY - WatermarkTop;
                var cropW = Math.Min(WatermarkSize - cropX, Size - destX);
                var cropH = Math.Min(WatermarkSize - cropY, Size - destY);
                if (cropW <= 0 || cropH <= 0) return;

                icon.Mutate(i => i.Crop(new Rectangle(cropX, cropY, cropW, cropH)));
                image.Mutate(ctx => ctx.DrawImage(icon, new Point(destX, destY), WatermarkOpacity));
            }
        }
        catch (Exception ex) { IconFailed(reference, ex); }
    }

    // Non-fatal: the key keeps its background and text, only the icon is missing.
    internal void IconFailed(string reference, Exception ex)
    {
        if (_loggedIconFailures.TryAdd($"{reference}|{ex.GetType().FullName}", 0))
            logger?.LogWarning(ex, "Could not draw icon '{Reference}'; rendering the key without it", reference);
    }

    // Replace RGB of every non-transparent pixel with ink, preserving alpha.
    private static void Tint(Image<Rgba32> image, Color ink)
    {
        var c = ink.ToPixel<Rgba32>();
        image.ProcessPixelRows(accessor =>
        {
            for (int yy = 0; yy < accessor.Height; yy++)
            {
                var row = accessor.GetRowSpan(yy);
                for (int xx = 0; xx < row.Length; xx++)
                {
                    ref Rgba32 p = ref row[xx];
                    if (p.A == 0) continue;
                    p.R = c.R; p.G = c.G; p.B = c.B;
                }
            }
        });
    }

    private const float BlinkOffBrightness = 0.25f;

    private static void Darken(Image<Rgba32> image, float factor)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref Rgba32 px = ref row[x];
                    px.R = (byte)(px.R * factor);
                    px.G = (byte)(px.G * factor);
                    px.B = (byte)(px.B * factor);
                }
            }
        });
    }

    private static void Desaturate(Image<Rgba32> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref Rgba32 px = ref row[x];
                    double grey = 0.2126 * px.R + 0.7152 * px.G + 0.0722 * px.B;
                    px.R = (byte)(px.R + 0.6 * (grey - px.R));
                    px.G = (byte)(px.G + 0.6 * (grey - px.G));
                    px.B = (byte)(px.B + 0.6 * (grey - px.B));
                }
            }
        });
    }

    private const int MarkerSize = 14;
    private const string ClockMarkerIcon = "builtin:clock-outline";
    private const string OfflineMarkerIcon = "builtin:link-variant-off";

    private void DrawStatusMarker(Image<Rgba32> image, string reference, Color ink)
    {
        // Resolve the glyph at full canvas size: icons come back as a 72x72 canvas that is then
        // resized to the marker size, so asking for a 14 px glyph shrank it to about 3 px (#47).
        var icon = iconResolver.ResolveImage(reference, "", Size);
        if (icon is null) return;
        try
        {
            using (icon)
            {
                icon.Mutate(i => i.Resize(new ResizeOptions
                {
                    Size = new Size(MarkerSize, MarkerSize),
                    Sampler = KnownResamplers.Lanczos3,
                }));
                Tint(icon, ink);
                image.Mutate(ctx => ctx.DrawImage(icon, new Point(Size - MarkerSize - 4, 4), 1f));
            }
        }
        catch (Exception ex) { IconFailed(reference, ex); }
    }

    private static void DrawCentredText(Image<Rgba32> image, string text, Font font, Color colour, float x, float baseline)
    {
        var opts = new RichTextOptions(font)
        {
            Origin = new Vector2(x, baseline),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        image.Mutate(ctx => ctx.DrawText(opts, text, colour));
    }

    // sRGB perceived luminance (no gamma) — fast and correct for a binary ink decision.
    private static Color Ink(Color bg)
    {
        var p = bg.ToPixel<Rgba32>();
        double L = (0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B) / 255.0;
        return L >= 0.45 ? InkDark : Color.White;
    }

    private static Color WithAlpha(Color colour, float alpha)
    {
        var p = colour.ToPixel<Rgba32>();
        return new Color(new Rgba32(p.R, p.G, p.B, (byte)(alpha * 255)));
    }

    // Splits "23.5 °C" → ("23.5", "°C"); "ON" → ("ON", null).
    private static (string Head, string? Tail) SplitValueUnit(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ("", null);
        var i = value.IndexOf(' ');
        return i < 0 ? (value, null) : (value[..i], value[(i + 1)..]);
    }

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    private static Color SampleBackground(Image<Rgba32> image)
    {
        var p = image[1, 1];
        return new Color(p);
    }

    private static Color? ParseColour(string? hex)
    {
        if (hex is null || hex.Length < 7 || hex[0] != '#') return null;
        try
        {
            var r = Convert.ToByte(hex.Substring(1, 2), 16);
            var g = Convert.ToByte(hex.Substring(3, 2), 16);
            var b = Convert.ToByte(hex.Substring(5, 2), 16);
            return new Color(new Rgba32(r, g, b));
        }
        catch { return null; }
    }
}
