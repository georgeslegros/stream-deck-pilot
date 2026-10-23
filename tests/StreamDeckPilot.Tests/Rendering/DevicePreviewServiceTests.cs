using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenMacroBoard.SDK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Rendering;

/// <summary>
/// The live preview (#27) shows exactly what the deck shows for the page on screen (the frames last
/// sent), composes other pages from desired state with the same looks, and never touches the hardware.
/// </summary>
public sealed class DevicePreviewServiceTests : IAsyncDisposable
{
    private const string Serial = "SN_PREVIEW";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private static readonly Dictionary<string, IReadOnlyList<ButtonAction>> NoGestures = new();

    private readonly FakeMacroBoard _board = new() { Serial = Serial };
    private readonly DesiredStateStore _desired = new();
    private readonly PaintedFrameStore _frames = new();
    private readonly BrokerIndicator _broker = new(TimeSpan.Zero);
    private readonly KeyBitmapComposer _composer;
    private DeviceSupervisorService? _supervisor;

    public DevicePreviewServiceTests()
    {
        Directory.CreateDirectory(_dir);
        var custom = new CustomImageSource(Options.Create(new StorageOptions { BaseDirectory = _dir }));
        _composer = new KeyBitmapComposer(new IconResolver(custom, NullLogger<IconResolver>.Instance));
    }

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null) await _supervisor.StopAsync(CancellationToken.None);
        Directory.Delete(_dir, recursive: true);
    }

    private static ButtonDefinition Button(string id, int key, string page, string label, string? colour = null) =>
        new(id, key, page, new DisplaySpec(Center: new TextZone(label, "{value}")),
            Inbound: colour is null ? null : new InboundBinding($"sensors/{id}", null, null, false, null),
            Rules: colour is null ? [] : [new ConditionalRule(">=0", colour, null)],
            Gestures: NoGestures);

    private static readonly DeviceConfig Config = new(2, Serial, [
        new ButtonGridPage("main", [Button("a", 0, "main", "A", "#0066AA"), Button("b", 6, "main", "B")]),
        new ButtonGridPage("other", [Button("c", 2, "other", "C", "#CC3300")]),
    ]);

    private async Task<DevicePreviewService> StartAsync()
    {
        var opts = Options.Create(new StorageOptions { BaseDirectory = _dir });
        var configs = new ConfigStore(opts);
        var catalogue = new CatalogueStore(opts);
        await configs.SaveAsync(Config);

        var renderer = new DeviceRenderer(_composer, broker: _broker, frames: _frames);
        _supervisor = new DeviceSupervisorService(new FakeStreamDeckLibrary(_board), catalogue, configs, _desired,
            new ActivePageStore(), renderer, NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60));
        await _supervisor.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_board.RenderedKeyCount() < _board.Keys.Count || _supervisor.GetActivePage(Serial) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the deck's initial paint did not finish");
            await Task.Delay(10);
        }
        return new DevicePreviewService(catalogue, configs, _desired, _frames, _composer, renderer, _supervisor);
    }

    private static Image<Rgba32> Load(PreviewResult result)
    {
        Assert.Equal(PreviewStatus.Ok, result.Status);
        return Image.Load<Rgba32>(result.Image!.Png);
    }

    private static Image<Rgba32> Tile(Image<Rgba32> board, int key) =>
        board.Clone(c => c.Crop(new Rectangle(BoardPreviewRenderer.CellOrigin(key, 5, 1).X,
            BoardPreviewRenderer.CellOrigin(key, 5, 1).Y, BoardPreviewRenderer.KeySize, BoardPreviewRenderer.KeySize)));

    private static Image<Rgba32> ToImage(KeyBitmap bitmap)
    {
        var access = (IKeyBitmapDataAccess)bitmap;
        return Image.LoadPixelData<Bgr24>(access.GetData(), access.Width, access.Height).CloneAs<Rgba32>();
    }

    private static void AssertSameRgb(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        Assert.Equal(expected.Size(), actual.Size());
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var (e, a) = (expected[x, y], actual[x, y]);
            Assert.True(e.R == a.R && e.G == a.G && e.B == a.B, $"pixel ({x},{y}) differs: {e} vs {a}");
        }
    }

    // ── The page on screen: the frames sent to the deck ─────────────────────

    [Fact]
    public async Task ActivePage_OfAConnectedDeck_IsExactlyWhatWasSentToTheKeys()
    {
        var preview = await StartAsync();

        var result = await preview.RenderBoardAsync(Serial);
        using var board = Load(result);

        Assert.Equal(PreviewSource.Device, result.Image!.Source);
        Assert.Equal("main", result.Image.PageId);
        Assert.Equal((5 * 72 + 6 * 6, 3 * 72 + 4 * 6), (board.Width, board.Height));
        foreach (var key in new[] { 0, 6 })
        {
            using var sent = ToImage(_board.RenderCalls.Last(c => c.KeyIndex == key).Bitmap);
            using var tile = Tile(board, key);
            AssertSameRgb(sent, tile);
        }
        using var empty = Tile(board, 1);
        Assert.Equal(new Rgba32(0, 0, 0), empty[36, 36]);   // no button: black, as on the device
    }

    [Fact]
    public async Task KeyPreview_EqualsTheComposerOutputForThatState()
    {
        var preview = await StartAsync();

        using var key = Load(await preview.RenderKeyAsync(Serial, 0));
        using var composed = _composer.ComposeImage(_desired.Get(Serial, "main", 0)!, Serial);
        AssertSameRgb(composed, key);
    }

    [Fact]
    public async Task OffscreenPage_IsComposedFromDesiredState()
    {
        var preview = await StartAsync();
        _desired.Set(Serial, "other", 2, _desired.Get(Serial, "other", 2)! with { CenterText = "42" });

        var result = await preview.RenderKeyAsync(Serial, 2, "other");
        using var key = Load(result);

        Assert.Equal(PreviewSource.DesiredState, result.Image!.Source);
        using var composed = _composer.ComposeImage(_desired.Get(Serial, "other", 2)!, Serial);
        AssertSameRgb(composed, key);
    }

    [Fact]
    public async Task OffscreenPage_GetsTheBrokerOfflineLook_LikeTheDeckWould()
    {
        var preview = await StartAsync();
        _broker.ReportDisconnected();

        using var key = Load(await preview.RenderKeyAsync(Serial, 2, "other"));
        using var offline = _composer.ComposeImage(_desired.Get(Serial, "other", 2)! with { IsOffline = true }, Serial);
        AssertSameRgb(offline, key);
    }

    [Fact]
    public async Task DisconnectedDeck_PreviewsFromDesiredState()
    {
        var preview = await StartAsync();
        _board.SimulateDisconnect();

        var result = await preview.RenderBoardAsync(Serial);
        Assert.Equal(PreviewSource.DesiredState, result.Image!.Source);
        Assert.Equal("main", result.Image.PageId);
    }

    [Fact]
    public async Task Preview_NeverTouchesTheHardware()
    {
        var preview = await StartAsync();
        var paints = _board.RenderCallCount();
        var brightness = _board.Brightness;

        await preview.RenderBoardAsync(Serial);
        await preview.RenderBoardAsync(Serial, "other", 2);
        await preview.RenderKeyAsync(Serial, 0);

        Assert.Equal(paints, _board.RenderCallCount());
        Assert.Equal(brightness, _board.Brightness);
    }

    [Fact]
    public async Task Scale2_DoublesTheImage()
    {
        var preview = await StartAsync();

        using var board = Load(await preview.RenderBoardAsync(Serial, scale: 2));
        using var key = Load(await preview.RenderKeyAsync(Serial, 0, scale: 2));

        Assert.Equal((2 * (5 * 72 + 6 * 6), 2 * (3 * 72 + 4 * 6)), (board.Width, board.Height));
        Assert.Equal((144, 144), (key.Width, key.Height));
    }

    [Fact]
    public async Task ETag_IsStable_AndChangesWithTheContent()
    {
        var preview = await StartAsync();

        var first = (await preview.RenderKeyAsync(Serial, 2, "other")).Image!.ETag;
        Assert.Equal(first, (await preview.RenderKeyAsync(Serial, 2, "other")).Image!.ETag);

        _desired.Set(Serial, "other", 2, _desired.Get(Serial, "other", 2)! with { CenterText = "99" });
        Assert.NotEqual(first, (await preview.RenderKeyAsync(Serial, 2, "other")).Image!.ETag);
    }

    [Fact]
    public async Task UnknownDevice_UnknownPage_AndKeyOutOfRange_AreReported()
    {
        var preview = await StartAsync();

        Assert.Equal(PreviewStatus.UnknownDevice, (await preview.RenderBoardAsync("NOPE")).Status);
        Assert.Equal(PreviewStatus.UnknownPage, (await preview.RenderBoardAsync(Serial, "nope")).Status);
        Assert.Equal(PreviewStatus.KeyOutOfRange, (await preview.RenderKeyAsync(Serial, 15)).Status);
        Assert.Equal(PreviewStatus.KeyOutOfRange, (await preview.RenderKeyAsync(Serial, -1)).Status);
    }

    // ── PaintedFrameStore ───────────────────────────────────────────────────

    [Fact]
    public void PaintedFrames_RaiseAnEventOnlyWhenTheContentChanges()
    {
        var store = new PaintedFrameStore();
        var events = new List<(string Serial, int Key, string Hash)>();
        store.Painted += (serial, key, frame) => events.Add((serial, key, frame.Hash));

        store.Record("S", 0, KeyBitmap.Create.FromRgb(10, 20, 30));
        store.Record("S", 0, KeyBitmap.Create.FromRgb(10, 20, 30));   // same pixels, new instance
        store.Record("S", 0, KeyBitmap.Create.FromRgb(10, 20, 31));
        store.Record("S", 0, KeyBitmap.Black);

        Assert.Equal(3, events.Count);
        Assert.Equal("black", events[^1].Hash);
        Assert.Equal(16, events[0].Hash.Length);
        Assert.NotEqual(events[0].Hash, events[1].Hash);
    }
}
