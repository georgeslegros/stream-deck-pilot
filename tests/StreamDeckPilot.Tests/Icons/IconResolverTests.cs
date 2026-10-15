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

    // Configs saved before #76's validation (or edited by hand) can still hold such names: the
    // source refuses anything that would resolve outside the device's images directory.
    [Theory]
    [InlineData("SN1", "../../secret.png")]
    [InlineData("SN1", "../SN2/icon.png")]
    [InlineData("SN1", "sub/icon.png")]
    [InlineData("SN1", "..")]
    [InlineData("..", "secret.png")]
    public async Task CustomImageSource_RefusesNamesOutsideTheImagesDirectory(string serial, string filename)
    {
        var png = TestImages.Encode("png");
        await File.WriteAllBytesAsync(Path.Combine(_dir, "secret.png"), png);
        Directory.CreateDirectory(Path.Combine(_dir, "images", "SN2"));
        await File.WriteAllBytesAsync(Path.Combine(_dir, "images", "SN2", "icon.png"), png);
        Directory.CreateDirectory(Path.Combine(_dir, "images", "SN1", "sub"));
        await File.WriteAllBytesAsync(Path.Combine(_dir, "images", "SN1", "sub", "icon.png"), png);

        Assert.Null(_custom.Load(serial, filename));
        Assert.Null(_custom.Load(serial, Path.Combine(_dir, "secret.png")));
        Assert.False(_custom.Delete(serial, filename));
        Assert.True(File.Exists(Path.Combine(_dir, "secret.png")));
        await Assert.ThrowsAsync<ArgumentException>(() => _custom.SaveAsync(serial, filename, new MemoryStream(png)));
    }

    [Theory]
    [InlineData("custom:../../secret.png")]
    [InlineData("custom:/etc/passwd")]
    public void ResolveImage_CustomOutsideImagesDirectory_UsesFallback(string reference)
    {
        File.WriteAllBytes(Path.Combine(_dir, "secret.png"), TestImages.Encode("png"));

        using var image = _resolver.ResolveImage(reference, "SN1", 72);

        Assert.NotNull(image);
        Assert.NotEqual(TestImages.Size, image.Width);
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

    // Keys render from several threads at once; the generated-icon cache must cope (#49).
    [Fact]
    public async Task GeneratedIcons_LoadConcurrently()
    {
        var names = GeneratedIconSource.KnownNames.ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i =>
            Task.Run(() => GeneratedIconSource.Load(names[i % names.Length]))));

        Assert.All(results, bytes => Assert.Equal(0x89, bytes![0]));
        foreach (var name in names)
            Assert.Same(GeneratedIconSource.Load(name), GeneratedIconSource.Load(name));
    }

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
