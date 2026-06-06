using System.Numerics;
using OpenMacroBoard.SDK;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Icons;

namespace StreamDeckPilot.Infrastructure.Rendering;

public sealed class KeyBitmapComposer(IconResolver iconResolver)
{
    private const int Size = 72;

    private static readonly Color DefaultBg = new(new Rgba32(55, 55, 55));
    private static readonly Color InkDark = new(new Rgba32(26, 26, 26));

    // Role-based type ramp (px).
    private static readonly Font? ValueFont = GeneratedIconSource.TryLoadFont(30f, FontStyle.Bold);
    private static readonly Font? ValueFontSmall = GeneratedIconSource.TryLoadFont(26f, FontStyle.Bold);
    private static readonly Font? UnitFont = GeneratedIconSource.TryLoadFont(14f);
    private static readonly Font? ToggleLabelFont = GeneratedIconSource.TryLoadFont(13f);

    public KeyBitmap Compose(ButtonRenderState state, string serial)
    {
        using var image = new Image<Rgba32>(Size, Size);

        var bg = ParseColour(state.BackgroundColour) ?? DefaultBg;
        image.Mutate(ctx => ctx.Fill(bg));

        // Stale tiles desaturate the background first so ink is computed on the drained colour.
        if (state.IsDimmed)
        {
            Desaturate(image);
            bg = SampleBackground(image);
        }

        var ink = Ink(bg);
        var isSensor = state.IsSensor;
        var hasLabel = !string.IsNullOrEmpty(state.LabelText);

        if (isSensor)
            RenderSensor(image, state, serial, ink);
        else if (hasLabel)
            RenderToggle(image, state, serial, ink);
        else
            RenderIconOnly(image, state, serial, ink);

        // Staleness marker: small clock glyph, top-right.
        if (state.IsDimmed)
            DrawClockMarker(image, ink);

        return KeyBitmap.Create.FromImageSharpImage(image);
    }

    // --- Layout A: sensor (big value hero) ----------------------------------
    private void RenderSensor(Image<Rgba32> image, ButtonRenderState state, string serial, Color ink)
    {
        DrawIcon(image, state.IconReference, serial, ink, size: 20, x: 6, y: 6, centred: false);

        var (head, tail) = SplitValueUnit(state.LabelText);

        if (!string.IsNullOrEmpty(head) && ValueFont is not null)
        {
            var font = head.Length >= 5 && ValueFontSmall is not null ? ValueFontSmall : ValueFont;
            // 1px black shadow keeps the value legible near the luminance threshold.
            DrawCentredText(image, head, font, Color.Black, x: 36f, baseline: 45f);
            DrawCentredText(image, head, font, ink, x: 36f, baseline: 44f);
        }

        if (!string.IsNullOrEmpty(tail) && UnitFont is not null)
            DrawCentredText(image, tail, UnitFont, WithAlpha(ink, 0.75f), x: 36f, baseline: 60f);
    }

    // --- Layout B: toggle (icon hero + caption) -----------------------------
    private void RenderToggle(Image<Rgba32> image, ButtonRenderState state, string serial, Color ink)
    {
        DrawIcon(image, state.IconReference, serial, ink, size: 40, x: 16, y: 8, centred: true);

        var text = Truncate(state.LabelText!, 9);
        if (ToggleLabelFont is not null)
            DrawCentredText(image, text, ToggleLabelFont, ink, x: 36f, baseline: 64f);
    }

    // --- Layout C: icon-only ------------------------------------------------
    private void RenderIconOnly(Image<Rgba32> image, ButtonRenderState state, string serial, Color ink)
    {
        DrawIcon(image, state.IconReference, serial, ink, size: 56, x: 8, y: 8, centred: true);
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
