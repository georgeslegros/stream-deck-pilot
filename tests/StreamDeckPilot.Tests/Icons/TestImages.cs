using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace StreamDeckPilot.Tests.Icons;

/// <summary>Small real images in every format ImageSharp can write, for format-restriction tests.</summary>
public static class TestImages
{
    public const int Size = 10;

    public static byte[] Encode(string format)
    {
        IImageEncoder encoder = format switch
        {
            "png" => new PngEncoder(),
            "jpeg" => new JpegEncoder(),
            "tiff" => new TiffEncoder(),
            "bmp" => new BmpEncoder(),
            "gif" => new GifEncoder(),
            "webp" => new WebpEncoder(),
            "tga" => new TgaEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
        using var image = new Image<Rgba32>(Size, Size, new Rgba32(200, 30, 30));
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }
}
