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

    /// <summary>
    /// Largest width or height accepted for a custom icon. Keys are 72–96 px, so this leaves ample
    /// room, while a small, highly compressed file can no longer declare a canvas that takes
    /// gigabytes to decode (#77).
    /// </summary>
    public const int MaxDimension = 1024;

    /// <summary>
    /// Reads the image's dimensions from its header without decoding the pixels. Null when the
    /// content isn't a PNG or JPEG, or its header can't be read.
    /// </summary>
    public static (int Width, int Height)? Identify(byte[] content) =>
        Identify(new MemoryStream(content, writable: false));

    /// <inheritdoc cref="Identify(byte[])"/>
    public static (int Width, int Height)? Identify(Stream content)
    {
        try
        {
            var info = Image.Identify(Configuration, content);
            return info is null ? null : (info.Width, info.Height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the dimensions are within <see cref="MaxDimension"/> on both sides.</summary>
    public static bool IsWithinLimits((int Width, int Height) size) =>
        size.Width is > 0 and <= MaxDimension && size.Height is > 0 and <= MaxDimension;

    private static Configuration CreateConfiguration()
    {
        var configuration = new Configuration(new PngConfigurationModule(), new JpegConfigurationModule());
        configuration.ImageFormatsManager.SetDecoder(PngFormat.Instance, new PngDecoder { IgnoreMetadata = true });
        configuration.ImageFormatsManager.SetDecoder(JpegFormat.Instance, new JpegDecoder { IgnoreMetadata = true });
        return configuration;
    }
}
