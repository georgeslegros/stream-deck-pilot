using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace StreamDeckPilot.Infrastructure.Icons;

/// <summary>
/// The only image formats the service decodes: PNG and JPEG. ImageSharp's default configuration
/// also registers TIFF, BMP, GIF, WebP, TGA and PBM decoders and picks one by sniffing the content,
/// so a file named <c>.png</c> could reach any of them. Decoding through <see cref="Configuration"/>
/// keeps the other decoders (and their advisories, see #36) out of reach.
/// </summary>
public static class IconImageFormats
{
    public static Configuration Configuration { get; } =
        new(new PngConfigurationModule(), new JpegConfigurationModule());

    /// <summary>True when the content is a PNG or JPEG, judged by its header, not its name.</summary>
    public static bool IsSupported(ReadOnlySpan<byte> content) =>
        Image.DetectFormat(Configuration, content) is not null;
}
