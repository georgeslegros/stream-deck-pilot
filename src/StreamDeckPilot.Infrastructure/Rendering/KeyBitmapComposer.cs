using System.Numerics;
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

public sealed class KeyBitmapComposer(IconResolver iconResolver)
{
    private const int Size = 72;

    // ── Corner-icon watermark (experimental, tunable) ────────────────────────────
    // IconPlacement.Corner no longer draws a tiny top-left glyph; it draws a large, faint
    // watermark anchored to the tile's RIGHT edge, sitting UNDER the hero value and caption.
    // These consts are the only knobs — adjust freely.

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

    // Type ramp (px).
    private static readonly Font? ValueFont = GeneratedIconSource.TryLoadFont(25.5f, FontStyle.Bold);
    private static readonly Font? ValueFontSmall = GeneratedIconSource.TryLoadFont(22f, FontStyle.Bold);
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

        // Stale tiles desaturate the background first so ink is computed on the drained colour.
        if (state.IsDimmed)
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

        // Staleness marker: small clock glyph, top-right.
        if (state.IsDimmed)
            DrawClockMarker(image, ink);

        return image;
    }

    // Large hero value, centred. The value is split from its unit (first space) so the
    // number reads big and the unit sits small beneath it.
    private void DrawCenterValue(Image<Rgba32> image, string centerText, Color ink, bool hasBottom)
    {
        var (head, tail) = SplitValueUnit(centerText);
        var heroBaseline = hasBottom ? 40f : 44f;   // lift the hero when a caption is present so the rows fit

        if (!string.IsNullOrEmpty(head) && ValueFont is not null)
        {
            var font = head.Length >= 5 && ValueFontSmall is not null ? ValueFontSmall : ValueFont;
            // 1px black shadow keeps the value legible near the luminance threshold.
            DrawCentredText(image, head, font, Color.Black, x: 36f, baseline: heroBaseline + 1);
            DrawCentredText(image, head, font, ink, x: 36f, baseline: heroBaseline);
        }

        if (!string.IsNullOrEmpty(tail) && UnitFont is not null)
            DrawCentredText(image, tail, UnitFont, WithAlpha(ink, 0.75f), x: 36f, baseline: heroBaseline + 15);
    }

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
        catch { /* non-fatal: leave background visible */ }
    }

    // Large, faint, right-anchored watermark for IconPlacement.Corner. The glyph is resized and
    // tinted like a normal icon, then composited at WatermarkOpacity with its centre on the tile's
    // right edge so only the left half shows. Because the placement is partly outside the tile,
    // ImageSharp's DrawImage (which requires the overlay rectangle to lie within bounds) would
    // throw — so we crop the resized glyph to the visible region and composite that crop at the
    // clamped on-tile origin. Drawn before the hero text, so the text always sits on top.
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
        catch { /* non-fatal: leave background visible */ }
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

    private void DrawClockMarker(Image<Rgba32> image, Color ink)
    {
        var icon = iconResolver.ResolveImage("builtin:clock-outline", "", 14);
        if (icon is null) return;
        try
        {
            using (icon)
            {
                icon.Mutate(i => i.Resize(new ResizeOptions
                {
                    Size = new Size(14, 14),
                    Sampler = KnownResamplers.Lanczos3,
                }));
                Tint(icon, ink);
                image.Mutate(ctx => ctx.DrawImage(icon, new Point(Size - 18, 4), 1f));
            }
        }
        catch { /* non-fatal */ }
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
