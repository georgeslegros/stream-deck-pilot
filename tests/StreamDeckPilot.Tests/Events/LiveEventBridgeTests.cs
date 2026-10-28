using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Events;

/// <summary>
/// The runtime's own notifications reach the bus (#108), and a <see cref="LiveView"/> stays current
/// from them alone: a fake deck connects, paints, changes page and disconnects; the broker comes and goes.
/// </summary>
public sealed class LiveEventBridgeTests : IAsyncLifetime
{
    private const string Serial = "SN_LIVE";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly EventBus _bus = new(Options.Create(new EventsOptions { MaxPerSecondPerKey = 1000 }));
    private readonly FakeBroker _broker = new();
    private readonly BrokerIndicator _indicator = new(TimeSpan.Zero);
    private readonly BrightnessController _brightness = new(Options.Create(new BrightnessOptions()));
    private readonly ActivePageStore _pages = new();
    private readonly PaintedFrameStore _frames = new();
    private readonly LatestValues _values = new();
    private readonly FakeMacroBoard _board = new() { Serial = Serial };
    private DeviceSupervisorService _supervisor = null!;
    private LiveEventBridge _bridge = null!;
    private LiveSnapshotService _snapshots = null!;
    private ConfigStore _configs = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var storage = Options.Create(new StorageOptions { BaseDirectory = _dir });
        _configs = new ConfigStore(storage);
        var catalogue = new CatalogueStore(storage);
        await _configs.SaveAsync(new DeviceConfig(2, Serial, [new ButtonGridPage("main", []), new ButtonGridPage("other", [])]));

        _supervisor = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(_board), catalogue, _configs, new DesiredStateStore(), _pages,
            new DeviceRenderer(frames: _frames), NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60), brightness: _brightness);
        _bridge = new LiveEventBridge(_bus, Options.Create(new EventsOptions { MaxValueLength = 16 }), _supervisor,
            _pages, _frames, _broker, _indicator, _brightness, _values);
        _snapshots = new LiveSnapshotService(_bus, catalogue, _supervisor, _pages, _frames, _brightness, _broker, _indicator, _values);
        await _bridge.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.StopAsync(CancellationToken.None);
        await _bridge.StopAsync(CancellationToken.None);
        _bus.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static async Task<LiveEvent> NextAsync(EventSubscription subscription, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var e = await subscription.Reader.ReadAsync(timeout.Token);
            if (e.Type == type) return e;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task DeckLifecycle_IsPublished()
    {
        using var events = _bus.Subscribe();
        await _supervisor.StartAsync(CancellationToken.None);

        Assert.Equal(Serial, (await NextAsync(events, nameof(DeviceConnected))).Serial);
        var key = (KeyRendered)await NextAsync(events, nameof(KeyRendered));
        Assert.Equal(Serial, key.Serial);
        Assert.Equal("black", key.Hash);   // an empty page blanks every key

        _supervisor.SetActivePage(Serial, "other");
        Assert.Equal("other", ((PageChanged)await NextAsync(events, nameof(PageChanged))).PageId);

        _board.SimulateDisconnect();
        Assert.Equal(Serial, (await NextAsync(events, nameof(DeviceDisconnected))).Serial);
    }

    [Fact]
    public async Task Broker_Values_AndBrightness_ArePublished()
    {
        using var events = _bus.Subscribe();

        _broker.SetSession(true);
        await NextAsync(events, nameof(BrokerOnline));

        _broker.Receive("sensors/co2", "a long payload that will be cut");
        var value = (ValueReceived)await NextAsync(events, nameof(ValueReceived));
        Assert.Equal("sensors/co2", value.Topic);
        Assert.Equal("a long payload t", value.Value);   // Events:MaxValueLength = 16

        _broker.SetSession(false);
        Assert.False(((BrokerOffline)await NextAsync(events, nameof(BrokerOffline))).OfflineIndicator);
        _indicator.ReportDisconnected();   // no delay configured: the deck shows offline at once
        Assert.True(((BrokerOffline)await NextAsync(events, nameof(BrokerOffline))).OfflineIndicator);

        _brightness.SetDesired(25);
        Assert.Equal(25, ((BrightnessChanged)await NextAsync(events, nameof(BrightnessChanged))).Level);
    }

    [Fact]
    public async Task LiveView_FollowsTheDeck_WithoutPolling()
    {
        await _supervisor.StartAsync(CancellationToken.None);
        await WaitForAsync(() => _supervisor.GetActivePage(Serial) == "main", "deck connected");
        // The active page is set before the connect-time paint, which records each key as it goes:
        // wait for all of them, or the snapshot can catch it halfway (as in #135).
        await WaitForAsync(() => Enumerable.Range(0, 15).All(k => _frames.Get(Serial, k) is not null), "first paint");

        using var view = new LiveView(_bus, _snapshots);
        var changes = 0;
        view.Changed += () => Interlocked.Increment(ref changes);
        await view.StartAsync();

        var device = view.Current!.Device(Serial)!;
        Assert.True(device.IsConnected);
        Assert.Equal("main", device.ActivePage);
        Assert.Equal(15, device.Keys.Count);

        _supervisor.SetActivePage(Serial, "other");
        _broker.SetSession(true);
        _broker.Receive("sensors/co2", "900");
        await WaitForAsync(() => view.Current!.Device(Serial)!.ActivePage == "other"
                                 && view.Current.Broker.Connected
                                 && view.Current.Values.ContainsKey("sensors/co2"), "view caught up");
        Assert.True(changes >= 3);

        _board.SimulateDisconnect();
        await WaitForAsync(() => !view.Current!.Device(Serial)!.IsConnected, "view saw the disconnect");
    }

    [Fact]
    public async Task LiveView_ResyncsAfterFallingBehind()
    {
        using var view = new LiveView(_bus, _snapshots);
        await view.StartAsync();

        // A burst far beyond its buffer: it must end on the latest state, not on a stale one.
        for (var level = 0; level < 5000; level++) _brightness.SetDesired(level % 100);
        _brightness.SetDesired(42);

        await WaitForAsync(() => view.Current!.Brightness == 42, "view resynchronised");
    }

    private sealed class FakeBroker : IBrokerSession
    {
        public bool IsSessionUp { get; private set; }
        public event Action<bool>? SessionChanged;
        public event Action<string, string>? ValueReceived;

        public void SetSession(bool up)
        {
            IsSessionUp = up;
            SessionChanged?.Invoke(up);
        }

        public void Receive(string topic, string payload) => ValueReceived?.Invoke(topic, payload);
    }
}
