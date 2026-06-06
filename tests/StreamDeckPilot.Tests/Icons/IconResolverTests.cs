using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
