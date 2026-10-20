using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenMacroBoard.SDK;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Mqtt.Pipeline;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Brightness;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Rendering;

/// <summary>Blinking keys (#23): the rule flag, the per-device scheduler and the paint-time phase.</summary>
public sealed class BlinkTests
{
    private const string Serial = "SN1";

    private static ButtonRenderState State(string colour, bool blink = false, bool dimmed = false) =>
        new("b", colour, null, IconPlacement.Corner, "42", null, IsDimmed: dimmed, DependsOnBroker: true, Blink: blink ? BlinkSpec.On : null);

    private static (BlinkScheduler Scheduler, DesiredStateStore Store, ManualTimeProvider Time) Build(int intervalMs = 500)
    {
        var store = new DesiredStateStore();
        var time = new ManualTimeProvider();
        var scheduler = new BlinkScheduler(store, Options.Create(new BlinkOptions { IntervalMilliseconds = intervalMs }), time);
        return (scheduler, store, time);
    }

    // The blink timer is periodic; the manual provider fires it once per call regardless of re-arming.
    private static void Tick(ManualTimeProvider time) => time.Timers[^1].FireStale();

    // ── Rule model ───────────────────────────────────────────────────────────

    [Fact]
    public void Rule_WithoutBlink_DeserialisesAsFalse_AndWritesNoBlinkProperty()
    {
        var rule = JsonSerializer.Deserialize<ConditionalRule>(
            """{ "condition": ">1000", "backgroundColour": "#CC3300", "icon": null }""", JsonOptions.Default)!;

        Assert.Null(rule.Blink);
        Assert.DoesNotContain("blink", JsonSerializer.Serialize(rule, JsonOptions.Default));
    }

    [Fact]
    public void Rule_WithBlink_RoundTrips()
    {
        var rule = JsonSerializer.Deserialize<ConditionalRule>(
            """{ "condition": "eq:failed", "backgroundColour": "#CC0000", "icon": null, "blink": true }""", JsonOptions.Default)!;

        Assert.Equal(BlinkSpec.On, rule.Blink);
        var json = JsonSerializer.Serialize(rule, JsonOptions.Default);
        Assert.Contains("\"blink\": true", json);
        Assert.Equal(rule, JsonSerializer.Deserialize<ConditionalRule>(json, JsonOptions.Default));
    }

    [Fact]
    public void MatchRule_ReturnsTheFirstMatchingRule_WithItsBlinkFlag()
    {
        IReadOnlyList<ConditionalRule> rules =
        [
            new(">1500", "#CC0000", null, Blink: BlinkSpec.On),
            new(">1000", "#FF8800", null),
        ];

        Assert.NotNull(InboundPipeline.MatchRule("1600", rules)!.Blink);
        Assert.Null(InboundPipeline.MatchRule("1200", rules)!.Blink);
        Assert.Null(InboundPipeline.MatchRule("800", rules));
    }

    [Fact]
    public void BlinkActive_IsOffForStaleKeys()
    {
        Assert.True(State("#CC0000", blink: true).BlinkActive);
        Assert.False(State("#CC0000", blink: true, dimmed: true).BlinkActive);
        Assert.False(State("#CC0000").BlinkActive);
    }

    // ── Scheduler ────────────────────────────────────────────────────────────

    [Fact]
    public void NoBlinkingKey_NoTimer()
    {
        var (scheduler, store, time) = Build();

        store.Set(Serial, "main", 0, State("#00FF00"));

        Assert.False(scheduler.IsRunning(Serial));
        Assert.Empty(time.Timers);
    }

    [Fact]
    public void BlinkingKey_StartsOneTimerPerDevice_AndTicksFlipThePhase()
    {
        var (scheduler, store, time) = Build(intervalMs: 400);
        var toggled = new List<string>();
        scheduler.Toggled += toggled.Add;

        store.Set(Serial, "main", 0, State("#CC0000", blink: true));
        store.Set(Serial, "main", 1, State("#CC0000", blink: true));

        Assert.True(scheduler.IsRunning(Serial));
        var timer = Assert.Single(time.Timers);
        Assert.Equal(TimeSpan.FromMilliseconds(400), timer.DueTime);
        Assert.False(scheduler.IsOffPhase(Serial)); // starts on the normal look

        Tick(time);
        Assert.True(scheduler.IsOffPhase(Serial));
        Tick(time);
        Assert.False(scheduler.IsOffPhase(Serial));
        Assert.Equal([Serial, Serial], toggled);
    }

    [Fact]
    public void RuleStopsMatching_TimerStops_AndPhaseResets()
    {
        var (scheduler, store, time) = Build();
        store.Set(Serial, "main", 0, State("#CC0000", blink: true));
        Tick(time);
        Assert.True(scheduler.IsOffPhase(Serial));

        store.Set(Serial, "main", 0, State("#00FF00"));

        Assert.False(scheduler.IsRunning(Serial));
        Assert.False(scheduler.IsOffPhase(Serial));
        Assert.Null(time.Timers[0].DueTime); // disposed
    }

    [Fact]
    public void LateCallbackFromAStoppedTimer_DoesNothing()
    {
        var (scheduler, store, time) = Build();
        var toggled = 0;
        scheduler.Toggled += _ => toggled++;
        store.Set(Serial, "main", 0, State("#CC0000", blink: true));
        store.Set(Serial, "main", 0, State("#00FF00"));
        store.Set(Serial, "main", 0, State("#CC0000", blink: true)); // a new timer

        time.Timers[0].FireStale(); // the first timer's callback, already dispatched when it was disposed

        Assert.Equal(0, toggled);
        Assert.False(scheduler.IsOffPhase(Serial));
    }

    [Fact]
    public void StaleBlinkingKey_DoesNotKeepTheTimerRunning()
    {
        var (scheduler, store, _) = Build();
        var blinking = State("#CC0000", blink: true);
        store.Set(Serial, "main", 0, blinking);

        store.TryUpdate(Serial, "main", 0, blinking, blinking with { IsDimmed = true });

        Assert.False(scheduler.IsRunning(Serial));
    }

    [Fact]
    public void BlinkOnAnotherDevice_IsIndependent()
    {
        var (scheduler, store, time) = Build();
        store.Set(Serial, "main", 0, State("#CC0000", blink: true));
        store.Set("SN2", "main", 0, State("#00FF00"));

        Tick(time);

        Assert.True(scheduler.IsOffPhase(Serial));
        Assert.False(scheduler.IsRunning("SN2"));
        Assert.False(scheduler.IsOffPhase("SN2"));
    }

    [Fact]
    public void Interval_IsClampedToTheFloor()
    {
        var (scheduler, _, _) = Build(intervalMs: 10);
        Assert.Equal(TimeSpan.FromMilliseconds(BlinkOptions.MinIntervalMilliseconds), scheduler.Interval);
    }

    // ── Renderer ─────────────────────────────────────────────────────────────

    [Fact]
    public void Renderer_PaintsTheOffPhase_OnlyForBlinkingKeys()
    {
        var (scheduler, store, time) = Build();
        var renderer = new DeviceRenderer(blink: scheduler);
        var board = new FakeMacroBoard();
        store.Set(Serial, "main", 0, State("#CC0000", blink: true));
        store.Set(Serial, "main", 1, State("#00FF00"));
        Tick(time);

        renderer.RenderLatest(board, Serial, "main", 0, store);
        renderer.RenderLatest(board, Serial, "main", 1, store);

        Assert.Equal(KeyBitmap.Create.FromRgb(30, 30, 30), board.RenderCalls[0].Bitmap); // dark half
        Assert.Equal(KeyBitmap.Create.FromRgb(0, 0xFF, 0), board.RenderCalls[1].Bitmap);  // untouched
    }

    [Fact]
    public void Renderer_OfflineLookWins_OverTheBlinkPhase()
    {
        var (scheduler, store, time) = Build();
        var broker = new BrokerIndicator(TimeSpan.Zero);
        broker.ReportDisconnected();
        var renderer = new DeviceRenderer(broker, scheduler);
        var board = new FakeMacroBoard();
        store.Set(Serial, "main", 0, State("#CC0000", blink: true));

        renderer.RenderLatest(board, Serial, "main", 0, store); // normal phase, already offline-grey
        Tick(time);
        renderer.RenderLatest(board, Serial, "main", 0, store); // off phase

        Assert.Equal(board.RenderCalls[0].Bitmap, board.RenderCalls[1].Bitmap);
    }

    // ── Supervisor: what a tick repaints ─────────────────────────────────────

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        public FakeMacroBoard Board { get; } = new() { Serial = Serial };
        public DesiredStateStore Store { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public BrightnessController Brightness { get; } = new(Options.Create(new BrightnessOptions()));
        public BrokerIndicator Broker { get; } = new(TimeSpan.Zero);
        public DeviceSupervisorService Supervisor { get; }

        public Rig()
        {
            Directory.CreateDirectory(_dir);
            var opts = Options.Create(new StorageOptions { BaseDirectory = _dir });
            var scheduler = new BlinkScheduler(Store, Options.Create(new BlinkOptions()), Time);
            Supervisor = new DeviceSupervisorService(
                new FakeStreamDeckLibrary(Board), new CatalogueStore(opts), new ConfigStore(opts), Store,
                new ActivePageStore(), new DeviceRenderer(Broker, scheduler), NullLogger<DeviceSupervisorService>.Instance,
                pollInterval: TimeSpan.FromMinutes(60), brightness: Brightness, brokerIndicator: Broker, blink: scheduler);
        }

        public async Task StartOnPageAsync(string pageId)
        {
            await Supervisor.StartAsync(CancellationToken.None);
            for (var i = 0; i < 200 && Supervisor.GetState(Serial) != DeviceConnectionState.Connected; i++)
                await Task.Delay(20);
            Assert.Equal(DeviceConnectionState.Connected, Supervisor.GetState(Serial));
            Supervisor.SetActivePage(Serial, pageId);
            Board.ClearRenderCalls();
        }

        public void Tick() => Time.Timers[^1].FireStale();

        public async ValueTask DisposeAsync()
        {
            await Supervisor.StopAsync(CancellationToken.None);
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Tick_RepaintsOnlyBlinkingKeysOnTheActivePage()
    {
        await using var rig = new Rig();
        rig.Store.Set(Serial, "main", 0, State("#CC0000", blink: true));
        rig.Store.Set(Serial, "main", 1, State("#00FF00"));
        rig.Store.Set(Serial, "other", 2, State("#CC0000", blink: true));
        await rig.StartOnPageAsync("main");

        rig.Tick();

        var (key, bitmap) = Assert.Single(rig.Board.RenderCalls);
        Assert.Equal(0, key);
        Assert.Equal(KeyBitmap.Create.FromRgb(30, 30, 30), bitmap);
    }

    [Fact]
    public async Task Tick_RepaintsOnlyKeysWhoseOwnFrameChanged()
    {
        // #131: a flash key next to a fast pulse isn't repainted on every pulse frame.
        await using var rig = new Rig();
        rig.Store.Set(Serial, "main", 0, State("#CC0000") with { Blink = new BlinkSpec(BlinkSpec.Flash, PeriodMs: 1000) });
        rig.Store.Set(Serial, "main", 1, State("#CC0000") with { Blink = new BlinkSpec(BlinkSpec.Pulse, PeriodMs: 1200) });
        await rig.StartOnPageAsync("main");

        rig.Tick(); // 100 ms: the pulse moved one frame, the flash is still in its first half

        var (key, _) = Assert.Single(rig.Board.RenderCalls);
        Assert.Equal(1, key);
    }

    [Fact]
    public async Task Tick_PaintsNothing_WhileTheDeckIsDark()
    {
        await using var rig = new Rig();
        rig.Store.Set(Serial, "main", 0, State("#CC0000", blink: true));
        await rig.StartOnPageAsync("main");
        rig.Brightness.SetDesired(0);
        rig.Board.ClearRenderCalls();

        rig.Tick();

        Assert.Empty(rig.Board.RenderCalls);
    }

    [Fact]
    public async Task Tick_PaintsNothing_WhileTheBrokerIsOffline()
    {
        await using var rig = new Rig();
        rig.Store.Set(Serial, "main", 0, State("#CC0000", blink: true));
        await rig.StartOnPageAsync("main");
        rig.Broker.ReportDisconnected();
        rig.Board.ClearRenderCalls();

        rig.Tick();

        Assert.Empty(rig.Board.RenderCalls);
    }
}
