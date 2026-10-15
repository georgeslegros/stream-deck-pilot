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

    [Fact]
    public void IconImageFormats_IdentifiesDimensionsFromTheHeaderOnly()
    {
        Assert.Equal((20000, 20000), IconImageFormats.Identify(TestImages.PngDeclaring(20000, 20000)));
        Assert.Null(IconImageFormats.Identify("not an image"u8.ToArray()));
        Assert.False(IconImageFormats.IsWithinLimits((IconImageFormats.MaxDimension + 1, 10)));
        Assert.True(IconImageFormats.IsWithinLimits((IconImageFormats.MaxDimension, IconImageFormats.MaxDimension)));
    }

    // A file on the volume (put there by hand, or uploaded before the limit) that declares a huge
    // canvas is refused from its header; decoding it would take gigabytes (#77).
    [Theory]
    [InlineData(20000, 20000)]
    [InlineData(IconImageFormats.MaxDimension + 1, 4)]
    public async Task ResolveImage_CustomOverDimensionLimit_UsesFallback(int width, int height)
    {
        // The small one is a real, decodable PNG: only the limit can make it fall back.
        var bytes = width > 10000 ? TestImages.PngDeclaring(width, height) : TestImages.Png(width, height, new Rgba32(200, 30, 30));
        await File.WriteAllBytesAsync(CustomPath("SN1", "big.png"), bytes);

        using var image = _resolver.ResolveImage("custom:big.png", "SN1", 72);
        using var fallback = _resolver.ResolveImage(null, "SN1", 72);

        Assert.NotNull(image);
        Assert.Equal(fallback!.Width, image.Width);
        Assert.NotEqual(width, image.Width);
    }

    [Fact]
    public async Task ResolveImage_LargeCustom_IsShrunkToCoverTheRequestedSize()
    {
        await File.WriteAllBytesAsync(CustomPath("SN1", "wide.png"), TestImages.Png(400, 200, new Rgba32(0, 0, 255)));

        using var image = _resolver.ResolveImage("custom:wide.png", "SN1", 72);

        Assert.NotNull(image);
        Assert.Equal((144, 72), (image.Width, image.Height));
    }

    // Re-renders used to read and decode the file every time. Same last-write time and length:
    // the cached decode is used, so content swapped in behind its back isn't read.
    [Fact]
    public async Task ResolveImage_SecondRender_DoesNotReReadTheFile()
    {
        var path = CustomPath("SN1", "icon.png");
        var red = TestImages.Png(TestImages.Size, TestImages.Size, new Rgba32(200, 30, 30));
        await File.WriteAllBytesAsync(path, red);
        using (var first = _resolver.ResolveImage("custom:icon.png", "SN1", 72))
            Assert.Equal(new Rgba32(200, 30, 30), first![0, 0]);

        var stamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, new byte[red.Length]); // same length, not an image
        File.SetLastWriteTimeUtc(path, stamp);

        using var second = _resolver.ResolveImage("custom:icon.png", "SN1", 72);
        Assert.NotNull(second);
        Assert.Equal(new Rgba32(200, 30, 30), second[0, 0]);

        // Callers mutate what they get (resize, tint): each call returns its own copy.
        using var third = _resolver.ResolveImage("custom:icon.png", "SN1", 72);
        Assert.NotSame(second, third);
    }

    [Fact]
    public async Task ResolveImage_ChangedFile_IsReadAgain()
    {
        var path = CustomPath("SN1", "icon.png");
        await File.WriteAllBytesAsync(path, TestImages.Png(TestImages.Size, TestImages.Size, new Rgba32(200, 30, 30)));
        using (var first = _resolver.ResolveImage("custom:icon.png", "SN1", 72))
            Assert.Equal(new Rgba32(200, 30, 30), first![0, 0]);

        await File.WriteAllBytesAsync(path, TestImages.Png(TestImages.Size, TestImages.Size, new Rgba32(0, 200, 0)));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        using var second = _resolver.ResolveImage("custom:icon.png", "SN1", 72);
        Assert.Equal(new Rgba32(0, 200, 0), second![0, 0]);
    }

    [Fact]
    public async Task Invalidate_DropsTheCachedDecode()
    {
        var path = CustomPath("SN1", "icon.png");
        var red = TestImages.Png(TestImages.Size, TestImages.Size, new Rgba32(200, 30, 30));
        await File.WriteAllBytesAsync(path, red);
        _resolver.ResolveImage("custom:icon.png", "SN1", 72)?.Dispose();

        var green = TestImages.Png(TestImages.Size, TestImages.Size, new Rgba32(0, 200, 0));
        Assert.Equal(red.Length, green.Length);
        var stamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, green);
        File.SetLastWriteTimeUtc(path, stamp);

        _resolver.Invalidate("SN1", "icon.png");

        using var image = _resolver.ResolveImage("custom:icon.png", "SN1", 72);
        Assert.Equal(new Rgba32(0, 200, 0), image![0, 0]);
    }

    private string CustomPath(string serial, string filename)
    {
        var dir = Path.Combine(_dir, "images", serial);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, filename);
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
