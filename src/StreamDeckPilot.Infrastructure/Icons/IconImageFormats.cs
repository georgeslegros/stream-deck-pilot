using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace StreamDeckPilot.Infrastructure.Icons;

/// <summary>
/// The only image formats the service decodes: PNG and JPEG. ImageSharp's default configuration
/// also registers TIFF, BMP, GIF, WebP, TGA, PBM, QOI and other decoders and picks one by sniffing
/// the content, so a file named <c>.png</c> could reach any of them. Decoding through
/// <see cref="DecoderOptions"/> keeps the other decoders out of reach. Metadata is skipped too
/// (JPEG ICC profiles are not read), and embedded profiles are never applied to the pixels.
/// ImageSharp 4 still keeps a PNG iCCP profile on the decoded image; nothing reads it.
/// </summary>
public static class IconImageFormats
{
    public static Configuration Configuration { get; } =
        new(new PngConfigurationModule(), new JpegConfigurationModule());

    public static DecoderOptions DecoderOptions { get; } = new()
    {
        Configuration = Configuration,
        SkipMetadata = true,
        // The default, made explicit: never convert pixels through an embedded profile.
        ColorProfileHandling = ColorProfileHandling.Preserve,
    };

    /// <summary>True when the content is a PNG or JPEG, judged by its header, not its name.</summary>
    public static bool IsSupported(ReadOnlySpan<byte> content)
    {
        try
        {
            Image.DetectFormat(DecoderOptions, content);
            return true;
        }
        catch (UnknownImageFormatException)
        {
            return false;
        }
    }
}
