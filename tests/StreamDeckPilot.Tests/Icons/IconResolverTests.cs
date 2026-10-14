using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Tests.Icons;

public sealed class IconResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly CustomImageSource _custom;
    private readonly IconResolver _resolver;

    public IconResolverTests()
    {
        Directory.CreateDirectory(_dir);
        _custom = new CustomImageSource(Options.Create(new StorageOptions { BaseDirectory = _dir }));
        _resolver = new IconResolver(_custom, NullLogger<IconResolver>.Instance);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Resolve_NullReference_ReturnsFallback()
    {
        var result = _resolver.Resolve(null, "SN1");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Resolve_KnownBuiltin_ReturnsPngBytes()
    {
        var result = _resolver.Resolve("builtin:thermometer", "SN1");
        Assert.NotNull(result);
        // PNG magic bytes
        Assert.Equal(0x89, result[0]);
        Assert.Equal(0x50, result[1]); // 'P'
        Assert.Equal(0x4E, result[2]); // 'N'
    }

    [Fact]
    public void Resolve_UnknownBuiltin_ReturnsFallback()
    {
        var result = _resolver.Resolve("builtin:nonexistent", "SN1");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task Resolve_ExistingCustom_ReturnsPngBytes()
    {
        // Write a minimal PNG-header file
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        await using (var s = new MemoryStream(png))
            await _custom.SaveAsync("SN1", "test.png", s);

        var result = _resolver.Resolve("custom:test.png", "SN1");
        Assert.Equal(png, result);
    }

    [Fact]
    public void Resolve_MissingCustom_ReturnsFallback()
    {
        var result = _resolver.Resolve("custom:missing.png", "SN1");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    public async Task ResolveImage_CustomPngOrJpeg_Decodes(string format)
    {
        await using (var s = new MemoryStream(TestImages.Encode(format)))
            await _custom.SaveAsync("SN1", "icon.png", s);

        using var image = _resolver.ResolveImage("custom:icon.png", "SN1", 72);

        Assert.NotNull(image);
        Assert.Equal(TestImages.Size, image.Width);
    }

    // Files already on disk are decoded with PNG and JPEG decoders only, whatever their name.
    [Theory]
    [InlineData("tiff")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("webp")]
    [InlineData("tga")]
    public async Task ResolveImage_CustomInOtherFormat_UsesFallback(string format)
    {
        await using (var s = new MemoryStream(TestImages.Encode(format)))
            await _custom.SaveAsync("SN1", "icon.png", s);

        using var image = _resolver.ResolveImage("custom:icon.png", "SN1", 72);
        using var fallback = _resolver.ResolveImage(null, "SN1", 72);

        Assert.NotNull(image);
        Assert.NotNull(fallback);
        Assert.NotEqual(TestImages.Size, image.Width);
        Assert.Equal(fallback.Width, image.Width);
    }

    [Theory]
    [InlineData("png", true)]
    [InlineData("jpeg", true)]
    [InlineData("tiff", false)]
    [InlineData("bmp", false)]
    [InlineData("gif", false)]
    [InlineData("webp", false)]
    [InlineData("tga", false)]
    public void IconImageFormats_AcceptsOnlyPngAndJpeg(string format, bool expected) =>
        Assert.Equal(expected, IconImageFormats.IsSupported(TestImages.Encode(format)));

    // Embedded ICC profiles are never parsed (ImageSharp ICC advisory, see #36). The default
    // configuration reading the profile proves the test file really carries one.
    [Fact]
    public void IconImageFormats_IgnoresJpegIccProfile()
    {
        var bytes = TestImages.JpegWithIccProfile();

        using (var unrestricted = Image.Load<Rgba32>(bytes))
            Assert.NotNull(unrestricted.Metadata.IccProfile);

        Assert.True(IconImageFormats.IsSupported(bytes));
        using var image = Image.Load<Rgba32>(IconImageFormats.Configuration, bytes);
        Assert.Null(image.Metadata.IccProfile);
        Assert.Equal(TestImages.Size, image.Width);
    }

    // ImageSharp 2.1 does not read iCCP chunks at all; this guards that a PNG carrying one still
    // decodes as an icon without a profile (e.g. after an ImageSharp upgrade).
    [Fact]
    public void IconImageFormats_IgnoresPngIccProfile()
    {
        var bytes = TestImages.PngWithIccProfile();

        Assert.True(IconImageFormats.IsSupported(bytes));
        using var image = Image.Load<Rgba32>(IconImageFormats.Configuration, bytes);
        Assert.Null(image.Metadata.IccProfile);
        Assert.Equal(TestImages.Size, image.Width);
    }

    [Fact]
    public void IconImageFormats_RejectsNonImageContent() =>
        Assert.False(IconImageFormats.IsSupported("not an image"u8));

    [Theory]
    [InlineData("thermometer")]
    [InlineData("humidity")]
    [InlineData("co2")]
    [InlineData("power")]
    [InlineData("home")]
    [InlineData("arrow-left")]
    [InlineData("arrow-right")]
    [InlineData("fallback")]
    [InlineData("placeholder")]
    public void AllBuiltinIconsResolve(string name)
    {
        var result = _resolver.Resolve($"builtin:{name}", "SN1");
        Assert.NotNull(result);
        Assert.Equal(0x89, result[0]); // PNG magic
    }
}
