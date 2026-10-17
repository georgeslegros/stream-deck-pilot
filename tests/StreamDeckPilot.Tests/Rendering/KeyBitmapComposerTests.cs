using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Rendering;

/// <summary>
/// The real key rendering path (the device renderer in other tests runs without a composer).
/// Structural tests assert properties that survive font and anti-aliasing differences; the golden
/// tests compare whole keys against committed baselines (see <see cref="GoldenImage"/>).
/// </summary>
public sealed class KeyBitmapComposerTests : IDisposable
{
    private const int Size = 72;
    private static readonly Rgba32 DefaultBackground = new(55, 55, 55);
    private static readonly Rgba32 DarkInk = new(26, 26, 26);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly KeyBitmapComposer _composer;

    public KeyBitmapComposerTests()
    {
        Directory.CreateDirectory(_dir);
        var custom = new CustomImageSource(Options.Create(new StorageOptions { BaseDirectory = _dir }));
        _composer = new KeyBitmapComposer(new IconResolver(custom, NullLogger<IconResolver>.Instance));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ButtonRenderState State(string? bg = null, string? icon = null,
        IconPlacement placement = IconPlacement.Corner, string? center = null, string? bottom = null,
        bool dimmed = false, bool offline = false, bool blinkOff = false) =>
        new("b", bg, icon, placement, center, bottom, dimmed, IsOffline: offline, Blink: blinkOff, IsBlinkOffPhase: blinkOff);

    // Pixels in a band that differ from the background by more than `threshold` on any channel.
    private static int InkPixels(Image<Rgba32> image, Rgba32 background, int yFrom, int yTo, int xFrom = 0, int xTo = Size,
        int threshold = 40)
    {
        var count = 0;
        for (var y = yFrom; y < yTo; y++)
        for (var x = xFrom; x < xTo; x++)
        {
            var p = image[x, y];
            if (Math.Max(Math.Max(Math.Abs(p.R - background.R), Math.Abs(p.G - background.G)), Math.Abs(p.B - background.B)) > threshold)
                count++;
        }
        return count;
    }

    private static void AssertSameImage(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            Assert.Equal(expected[x, y], actual[x, y]);
    }

    private static bool AllPixels(Image<Rgba32> image, Func<Rgba32, bool> predicate)
    {
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            if (!predicate(image[x, y])) return false;
        return true;
    }

    // ── Structure ────────────────────────────────────────────────────────────

    [Fact]
    public void Compose_ProducesA72PxKeyBitmap()
    {
        var bitmap = _composer.Compose(State("#0066AA", center: "21.6 °C"), "SN1");

        Assert.Equal(Size, bitmap.Width);
        Assert.Equal(Size, bitmap.Height);
    }

    [Fact]
    public void Background_IsTheRuleColour_OrTheDefault()
    {
        using var red = _composer.ComposeImage(State("#CC3300"), "SN1");
        using var none = _composer.ComposeImage(State(), "SN1");
        using var invalid = _composer.ComposeImage(State("red"), "SN1");

        Assert.True(AllPixels(red, p => p == new Rgba32(0xCC, 0x33, 0x00)));
        Assert.True(AllPixels(none, p => p == DefaultBackground));
        Assert.True(AllPixels(invalid, p => p == DefaultBackground));
    }

    [Fact]
    public void CenterText_IsDrawnInTheHeroBand_WithContrastingInk()
    {
        var dark = new Rgba32(0x00, 0x33, 0x66);
        var light = new Rgba32(0xFF, 0xD5, 0x4F);
        using var onDark = _composer.ComposeImage(State("#003366", center: "21.6 °C"), "SN1");
        using var onLight = _composer.ComposeImage(State("#FFD54F", center: "21.6 °C"), "SN1");

        // The hero value sits in the middle band; the unit below it.
        Assert.True(InkPixels(onDark, dark, 18, 46) > 80);
        Assert.True(InkPixels(onDark, dark, 47, 62) > 10);
        // Dark background → white ink; light background → dark ink, and never white.
        Assert.False(AllPixels(onDark, p => p != new Rgba32(255, 255, 255)));
        Assert.True(AllPixels(onLight, p => p.R < 250 || p.G < 250 || p.B < 250));
        Assert.True(InkPixels(onLight, light, 18, 46) > 80);
        Assert.False(AllPixels(onLight, p => Math.Abs(p.R - DarkInk.R) > 12 || Math.Abs(p.G - DarkInk.G) > 12));
    }

    // #48: long values shrink to fit instead of running off both edges.
    [Theory]
    [InlineData("12345")]
    [InlineData("1234.56")]
    [InlineData("-12.345")]
    [InlineData("unavailable")]
    public void CenterValue_NeverTouchesTheEdges(string value)
    {
        var bg = new Rgba32(0x33, 0xAA, 0x33);
        using var image = _composer.ComposeImage(State("#33AA33", center: value), "SN1");

        Assert.True(InkPixels(image, bg, 0, Size, 1, Size - 1) > 80);
        Assert.Equal(0, InkPixels(image, bg, 0, Size, 0, 1));
        Assert.Equal(0, InkPixels(image, bg, 0, Size, Size - 1, Size));
    }

    [Fact]
    public void BottomCaption_IsDrawnAlongTheBottom_Only()
    {
        var bg = new Rgba32(0x22, 0x22, 0x22);
        using var image = _composer.ComposeImage(State("#222222", bottom: "Bureau"), "SN1");

        Assert.True(InkPixels(image, bg, 58, 72) > 20);
        Assert.Equal(0, InkPixels(image, bg, 0, 54));
    }

    [Fact]
    public void LongCaption_IsTruncated()
    {
        using var full = _composer.ComposeImage(State("#222222", bottom: "Living room east"), "SN1");
        using var cut = _composer.ComposeImage(State("#222222", bottom: "Living roo…"), "SN1");

        // "Living room east" is cut to 10 characters plus an ellipsis.
        AssertSameImage(cut, full);
    }

    [Fact]
    public void CenterIcon_FillsTheMiddle_WhenThereIsNoCenterText()
    {
        var bg = new Rgba32(0x22, 0x22, 0x22);
        using var image = _composer.ComposeImage(State("#222222", "builtin:lightbulb", IconPlacement.Center), "SN1");

        Assert.True(InkPixels(image, bg, 10, 62, 10, 62) > 600);
    }

    [Fact]
    public void CenterIcon_YieldsToCenterText()
    {
        using var withIcon = _composer.ComposeImage(State("#222222", "builtin:lightbulb", IconPlacement.Center, center: "ON"), "SN1");
        using var without = _composer.ComposeImage(State("#222222", center: "ON"), "SN1");

        AssertSameImage(without, withIcon);
    }

    [Fact]
    public void CornerIcon_IsAFaintWatermarkBehindTheTile()
    {
        var bg = new Rgba32(0x00, 0x66, 0xAA);
        using var image = _composer.ComposeImage(State("#0066AA", "builtin:thermometer", IconPlacement.Corner), "SN1");

        // Visible over a large area, but faint: no pixel strays far from the background.
        Assert.True(InkPixels(image, bg, 0, Size, threshold: 8) > 300);
        Assert.Equal(0, InkPixels(image, bg, 0, Size, threshold: 70));
    }

    [Fact]
    public void Dimmed_DesaturatesTheBackground_AndShowsTheClockTopRight()
    {
        using var image = _composer.ComposeImage(State("#0066AA", dimmed: true), "SN1");
        var corner = image[1, 1];

        // Pulled towards grey: the channel spread shrinks (0x00/0xAA → much closer).
        Assert.True(corner.B - corner.R < 0xAA / 2);
        Assert.True(InkPixels(image, corner, 4, 18, Size - 18, Size - 4) > 15);
        Assert.Equal(0, InkPixels(image, corner, 20, Size));
    }

    // Until #47 is fixed, at least the desaturation (and the golden "stale" key) is covered.
    [Fact]
    public void Dimmed_DesaturatesTheBackground()
    {
        using var image = _composer.ComposeImage(State("#0066AA", dimmed: true), "SN1");
        var corner = image[1, 1];

        Assert.True(corner.B - corner.R < 0xAA / 2);
    }

    // #26: broker offline dims like stale but carries its own marker, so the two read differently.
    [Fact]
    public void Offline_DesaturatesTheBackground_WithAMarkerDistinctFromTheClock()
    {
        using var offline = _composer.ComposeImage(State("#0066AA", offline: true), "SN1");
        using var stale = _composer.ComposeImage(State("#0066AA", dimmed: true), "SN1");
        var corner = offline[1, 1];

        Assert.True(corner.B - corner.R < 0xAA / 2);
        Assert.True(InkPixels(offline, corner, 4, 18, Size - 18, Size - 4) > 15);
        Assert.Equal(0, InkPixels(offline, corner, 20, Size));

        var differing = 0;
        for (var y = 4; y < 18; y++)
        for (var x = Size - 18; x < Size - 4; x++)
            if (!offline[x, y].Equals(stale[x, y])) differing++;
        Assert.True(differing > 10, $"offline marker looks like the clock ({differing} px differ)");
    }

    [Fact]
    public void UnknownIcons_DoNotThrow()
    {
        using var builtin = _composer.ComposeImage(State("#222222", "builtin:does-not-exist", IconPlacement.Center), "SN1");
        using var custom = _composer.ComposeImage(State("#222222", "custom:missing.png", IconPlacement.Corner, center: "1"), "SN1");

        Assert.Equal(Size, builtin.Width);
        Assert.Equal(Size, custom.Width);
    }

    [Fact]
    public void BuiltinMdiIcon_ResolvesToAWhiteGlyph()
    {
        var custom = new CustomImageSource(Options.Create(new StorageOptions { BaseDirectory = _dir }));
        using var glyph = new IconResolver(custom, NullLogger<IconResolver>.Instance).ResolveImage("builtin:thermometer", "SN1", 66);

        Assert.NotNull(glyph);
        Assert.Equal(Size, glyph.Width);
        var opaque = 0;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            if (glyph[x, y].A > 128)
            {
                opaque++;
                Assert.Equal((255, 255, 255), (glyph[x, y].R, glyph[x, y].G, glyph[x, y].B));
            }
        Assert.True(opaque > 300);
    }

    [Fact]
    public void DeviceRenderer_WithComposer_SendsComposedKeysToTheBoard()
    {
        var board = new FakeMacroBoard();
        new DeviceRenderer(_composer).RenderButton(board, "SN1", 3, State("#CC3300", center: "1200 ppm"));

        var (key, bitmap) = Assert.Single(board.RenderCalls);
        Assert.Equal(3, key);
        Assert.Equal(Size, bitmap.Width);
    }

    // #52: an icon that fails to draw is logged (once per icon and error), not silently dropped.
    [Fact]
    public void IconFailures_AreLoggedOncePerIconAndError()
    {
        var logger = new ListLogger<KeyBitmapComposer>();
        var custom = new CustomImageSource(Options.Create(new StorageOptions { BaseDirectory = _dir }));
        var composer = new KeyBitmapComposer(new IconResolver(custom, NullLogger<IconResolver>.Instance), logger);

        composer.IconFailed("custom:broken.png", new InvalidOperationException("boom"));
        composer.IconFailed("custom:broken.png", new InvalidOperationException("boom again"));
        composer.IconFailed("custom:broken.png", new ArgumentException("other"));
        composer.IconFailed("builtin:other", new InvalidOperationException("boom"));

        Assert.Equal(3, logger.Warnings.Count);
        Assert.Contains("custom:broken.png", logger.Warnings[0]);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    // ── Golden keys ──────────────────────────────────────────────────────────

    public static TheoryData<string, ButtonRenderState> GoldenCases => new()
    {
        { "sensor", State(null, "builtin:thermometer", IconPlacement.Corner, "21.6 °C", "Bureau") },
        { "sensor-alert", State("#CC3300", "builtin:molecule-co2", IconPlacement.Corner, "1200 ppm", "CO2") },
        { "light-on", State("#FFD54F", "builtin:lightbulb", IconPlacement.Center, bottom: "Desk") },
        { "light-off", State("#222222", "builtin:lightbulb-outline", IconPlacement.Center, bottom: "Desk") },
        { "navigation", State(null, "builtin:chevron-right", IconPlacement.Center) },
        { "stale", State("#0066AA", "builtin:thermometer", IconPlacement.Corner, "19.2 °C", "Salon", dimmed: true) },
        { "long-value", State("#33AA33", null, IconPlacement.Corner, "12345 W", "Power") },
        { "text-state", State("#FF8800", null, IconPlacement.Corner, "heat", "Mode") },
        { "value-7-chars", State("#33AA33", null, IconPlacement.Corner, "1234.56 kWh", "Energy") },
        { "value-negative", State("#0066AA", "builtin:thermometer", IconPlacement.Corner, "-12.35 °C", "Outside") },
        { "offline", State("#0066AA", "builtin:thermometer", IconPlacement.Corner, "19.2 °C", "Salon", offline: true) },
        // #23: the dark half of a blink; the other half is "sensor-alert" itself.
        { "sensor-alert-blink-off", State("#CC3300", "builtin:molecule-co2", IconPlacement.Corner, "1200 ppm", "CO2", blinkOff: true) },
        { "sparkline", State("#0066AA", "builtin:molecule-co2", IconPlacement.Corner, "850 ppm", "CO2") with { Sparkline = RisingTrend } },
        { "sparkline-light", State("#FFD54F", null, IconPlacement.Corner, "2.4 kW", "Solar") with { Sparkline = RisingTrend } },
    };

    // ── Sparkline (#25) ──────────────────────────────────────────────────────

    private static readonly SparklineSeries RisingTrend = new([
        new(0, 420), new(0.2, 480), new(0.35, 650), new(0.6, 600), new(0.8, 850), new(1, 850),
    ], 420, 850);

    [Fact]
    public void Sparkline_StaysOutOfTheTopOfTheTile()
    {
        using var plain = _composer.ComposeImage(State("#0066AA", center: "850 ppm", bottom: "CO2"), "SN1");
        using var trend = _composer.ComposeImage(
            State("#0066AA", center: "850 ppm", bottom: "CO2") with { Sparkline = RisingTrend }, "SN1");

        for (var y = 0; y < 36; y++)
        for (var x = 0; x < Size; x++)
            Assert.Equal(plain[x, y], trend[x, y]);
        Assert.True(InkPixels(trend, new Rgba32(0, 102, 170), 38, Size, threshold: 20) >
                    InkPixels(plain, new Rgba32(0, 102, 170), 38, Size, threshold: 20));
    }

    [Fact]
    public void Sparkline_FlatSeries_DrawsAHorizontalLineAcrossTheBand()
    {
        // One value carried forward (a topic that published once): flat across the middle.
        var flat = new SparklineSeries([new(0, 500), new(1, 500)], 500, 500);
        using var image = _composer.ComposeImage(State("#0066AA") with { Sparkline = flat }, "SN1");

        var bg = new Rgba32(0, 102, 170);
        Assert.True(InkPixels(image, bg, 50, 52, threshold: 20) > 60);   // the line at y ≈ 51
        Assert.True(InkPixels(image, bg, 40, 48, threshold: 20) == 0);   // nothing above it
    }

    [Fact]
    public void Sparkline_StepStartsWhereTheDataStarts()
    {
        // A single point halfway through the window: nothing is drawn to its left.
        var half = new SparklineSeries([new(0.5, 500), new(1, 500)], 500, 500);
        using var image = _composer.ComposeImage(State("#0066AA") with { Sparkline = half }, "SN1");

        var bg = new Rgba32(0, 102, 170);
        Assert.Equal(0, InkPixels(image, bg, 38, Size, xFrom: 0, xTo: 33, threshold: 20));
        Assert.True(InkPixels(image, bg, 50, 52, xFrom: 38, xTo: Size, threshold: 20) > 25);
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void Golden(string name, ButtonRenderState state)
    {
        using var image = _composer.ComposeImage(state, "SN1");
        GoldenImage.AssertMatches(image, name);
    }
}
