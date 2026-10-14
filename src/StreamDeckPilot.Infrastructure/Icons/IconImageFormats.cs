using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace StreamDeckPilot.Infrastructure.Icons;

/// <summary>
/// The only image formats the service decodes: PNG and JPEG. ImageSharp's default configuration
/// also registers TIFF, BMP, GIF, WebP, TGA and PBM decoders and picks one by sniffing the content,
/// so a file named <c>.png</c> could reach any of them. Decoding through <see cref="Configuration"/>
/// keeps the other decoders (and their advisories, see #36) out of reach. Metadata is ignored too,
/// so embedded ICC profiles (PNG iCCP, JPEG APP2) are never parsed; icons don't need them.
/// </summary>
public static class IconImageFormats
{
    public static Configuration Configuration { get; } = CreateConfiguration();

    /// <summary>True when the content is a PNG or JPEG, judged by its header, not its name.</summary>
    public static bool IsSupported(ReadOnlySpan<byte> content) =>
        Image.DetectFormat(Configuration, content) is not null;

    private static Configuration CreateConfiguration()
    {
        var configuration = new Configuration(new PngConfigurationModule(), new JpegConfigurationModule());
        configuration.ImageFormatsManager.SetDecoder(PngFormat.Instance, new PngDecoder { IgnoreMetadata = true });
        configuration.ImageFormatsManager.SetDecoder(JpegFormat.Instance, new JpegDecoder { IgnoreMetadata = true });
        return configuration;
    }
}
