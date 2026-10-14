using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
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

    /// <summary>A PNG carrying an iCCP chunk (embedded ICC profile) right after IHDR.</summary>
    public static byte[] PngWithIccProfile()
    {
        var png = Encode("png");
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(MinimalIccProfile());

        // iCCP data: profile name, NUL, compression method 0 (deflate), zlib stream.
        var data = new List<byte>(Encoding.ASCII.GetBytes("icc"));
        data.Add(0);
        data.Add(0);
        data.AddRange(compressed.ToArray());

        const int afterIhdr = 8 + 4 + 4 + 13 + 4; // signature + IHDR length, type, data, CRC
        using var output = new MemoryStream();
        output.Write(png, 0, afterIhdr);
        WritePngChunk(output, "iCCP", data.ToArray());
        output.Write(png, afterIhdr, png.Length - afterIhdr);
        return output.ToArray();
    }

    /// <summary>A JPEG carrying an APP2 ICC_PROFILE segment.</summary>
    public static byte[] JpegWithIccProfile()
    {
        using var image = new Image<Rgba32>(Size, Size, new Rgba32(200, 30, 30));
        image.Metadata.IccProfile = new IccProfile(MinimalIccProfile());
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    // Header-only ICC v4 display profile with no tags.
    private static byte[] MinimalIccProfile()
    {
        var profile = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x04300000);
        Encoding.ASCII.GetBytes("mntr").CopyTo(profile, 12);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(profile, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(profile, 20);
        Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
        return profile;
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        output.Write(buffer);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeAndData));
        output.Write(buffer);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
