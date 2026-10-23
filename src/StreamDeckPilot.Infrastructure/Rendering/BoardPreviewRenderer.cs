using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StreamDeckPilot.Infrastructure.Rendering;

/// <summary>
/// Lays key images out as a Stream-Deck-style board: a dark bezel, a gap between keys, and black
/// for keys with nothing on them (an unconfigured key is black on the device too). Shared by the
/// live preview endpoint (#27) and the StreamDeckPreview documentation tool.
/// </summary>
public static class BoardPreviewRenderer
{
    public const int KeySize = 72;
    private const int GapPerScale = 6;
    private static readonly Rgba32 Bezel = new(28, 28, 28);

    /// <summary>
    /// Composes a <paramref name="columns"/> × <paramref name="rows"/> board. <paramref name="keyImage"/>
    /// returns the 72×72 image of a key, or null for a black key; the board owns and disposes what it
    /// returns. <paramref name="scale"/> is the number of board pixels per key pixel.
    /// </summary>
    public static Image<Rgba32> Compose(int columns, int rows, int scale, Func<int, Image<Rgba32>?> keyImage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);

        var gap = GapPerScale * scale;
        var cell = KeySize * scale;
        var board = new Image<Rgba32>(columns * cell + (columns + 1) * gap, rows * cell + (rows + 1) * gap, Bezel);

        for (var i = 0; i < columns * rows; i++)
        {
            var (x, y) = CellOrigin(i, columns, scale);
            using var key = keyImage(i);
            if (key is null)
            {
                board.Mutate(c => c.Fill(Color.Black, new RectangleF(x, y, cell, cell)));
                continue;
            }
            if (key.Width != cell || key.Height != cell)
                key.Mutate(c => c.Resize(cell, cell, KnownResamplers.Bicubic));
            board.Mutate(c => c.DrawImage(key, new Point(x, y), 1f));
        }
        return board;
    }

    /// <summary>Top-left corner of a key on a board composed by <see cref="Compose"/>.</summary>
    public static (int X, int Y) CellOrigin(int keyIndex, int columns, int scale)
    {
        var gap = GapPerScale * scale;
        var cell = KeySize * scale;
        return (gap + keyIndex % columns * (cell + gap), gap + keyIndex / columns * (cell + gap));
    }

    /// <summary>A key image scaled for a single-key preview (1 = the device's own 72×72).</summary>
    public static Image<Rgba32> Scale(Image<Rgba32> key, int scale)
    {
        if (scale != 1)
            key.Mutate(c => c.Resize(KeySize * scale, KeySize * scale, KnownResamplers.Bicubic));
        return key;
    }
}
