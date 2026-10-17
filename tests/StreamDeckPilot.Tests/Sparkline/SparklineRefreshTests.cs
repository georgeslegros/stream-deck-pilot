using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Sparkline;
using StreamDeckPilot.Infrastructure.Staleness;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Sparkline;

/// <summary>
/// Sparkline keys on the page on screen slide their time window without new values (on-change
/// topics), piggy-backing on the staleness tick, at most once per bucket and a minute apart.
/// </summary>
public sealed class SparklineRefreshTests : IDisposable
{
    private const string Serial = "SN_REFRESH";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public SparklineRefreshTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly Dictionary<string, IReadOnlyList<ButtonAction>> NoGestures = new();

    // 1 h window, 240 points → 15 s buckets, so the 60 s floor applies.
    private static ButtonDefinition Trend(string page = "main") => new("trend", 0, page,
        new DisplaySpec(Center: new TextZone(null, "{value}"), Sparkline: new SparklineSpec(1)),
        new InboundBinding("sensors/co2", null, null, false, null), [], NoGestures);

    private static ButtonDefinition Plain(string page = "main") => new("plain", 1, page,
        new DisplaySpec(Center: new TextZone(null, "{value}")),
        new InboundBinding("sensors/temp", null, null, false, null), [], NoGestures);

    private sealed record Rig(StalenessMonitor Monitor, SparklineHistory History, FakeMacroBoard Board,
        DesiredStateStore Desired, DeviceSupervisorService Supervisor);

    private async Task<Rig> BuildAsync(DeviceConfig config)
    {
        var opts = Options.Create(new StorageOptions { BaseDirectory = _dir });
        var configStore = new ConfigStore(opts);
        await configStore.SaveAsync(config);

        var desired = new DesiredStateStore();
        var board = new FakeMacroBoard { Serial = Serial };
        var supervisor = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(board), new CatalogueStore(opts), configStore, desired,
            new ActivePageStore(), new DeviceRenderer(), NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await supervisor.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None);

        var history = new SparklineHistory(Options.Create(new SparklineOptions()), _time);
        var monitor = new StalenessMonitor(configStore, new LastUpdatedStore(), desired, new DeviceRenderer(),
            supervisor, NullLogger<StalenessMonitor>.Instance, history);
        return new Rig(monitor, history, board, desired, supervisor);
    }

    // What the MQTT pipeline does when a value arrives.
    private static void Receive(Rig rig, ButtonDefinition button, string value) =>
        rig.Desired.Set(Serial, button.PageId, button.KeyIndex,
            new ButtonRenderState(button.ButtonId, null, null, IconPlacement.Corner, value, null,
                DependsOnBroker: true, Sparkline: rig.History.Record(Serial, button, value)));

    [Fact]
    public async Task SparseTopic_SlidesWithoutNewMessages()
    {
        var trend = Trend();
        var rig = await BuildAsync(new DeviceConfig(1, Serial, [new ButtonGridPage("main", [trend])]));
        Receive(rig, trend, "400");
        _time.Advance(TimeSpan.FromMinutes(1));
        Receive(rig, trend, "900");   // the step is at the right edge
        var before = rig.Desired.Get(Serial, "main", 0)!.Sparkline!;
        rig.Board.ClearRenderCalls();

        _time.Advance(TimeSpan.FromMinutes(30));
        await rig.Monitor.TickAsync();

        var after = rig.Desired.Get(Serial, "main", 0)!.Sparkline!;
        Assert.Equal(1, before.Points[^1].X);
        // Thirty minutes later the step from 400 to 900 has moved half a window to the left.
        Assert.Equal(0.5, after.Points.Single(p => p.Value == 900 && p.X < 1).X, 3);
        Assert.Equal(new SparklinePoint(1, 900), after.Points[^1]);
        Assert.Contains(rig.Board.RenderCalls, c => c.KeyIndex == 0);
    }

    [Fact]
    public async Task Refresh_IsRateLimited_ToOncePerBucketAndAMinute()
    {
        var trend = Trend();
        var rig = await BuildAsync(new DeviceConfig(1, Serial, [new ButtonGridPage("main", [trend])]));
        Receive(rig, trend, "400");
        rig.Board.ClearRenderCalls();

        _time.Advance(TimeSpan.FromSeconds(30));   // past a 15 s bucket, under the 60 s floor
        await rig.Monitor.TickAsync();
        Assert.Empty(rig.Board.RenderCalls);

        _time.Advance(TimeSpan.FromSeconds(30));
        await rig.Monitor.TickAsync();
        await rig.Monitor.TickAsync();             // same instant: no second repaint
        Assert.Single(rig.Board.RenderCalls, c => c.KeyIndex == 0);
    }

    [Fact]
    public async Task NoSparklineOnTheActivePage_NoRepaints()
    {
        var plain = Plain();
        var offPage = Trend("other");
        var rig = await BuildAsync(new DeviceConfig(1, Serial, [
            new ButtonGridPage("main", [plain]),
            new ButtonGridPage("other", [offPage]),
        ]));
        rig.Supervisor.SetActivePage(Serial, "main");
        Receive(rig, plain, "21");
        Receive(rig, offPage, "400");
        var offPageState = rig.Desired.Get(Serial, "other", 0);
        rig.Board.ClearRenderCalls();

        _time.Advance(TimeSpan.FromHours(1));
        await rig.Monitor.TickAsync();

        Assert.Empty(rig.Board.RenderCalls);
        Assert.Same(offPageState, rig.Desired.Get(Serial, "other", 0));
    }

    [Fact]
    public async Task RemovedButtons_LoseTheirHistory()
    {
        var trend = Trend();
        var rig = await BuildAsync(new DeviceConfig(1, Serial, [new ButtonGridPage("main", [Plain()])]));
        rig.History.Record(Serial, trend, "400");   // history for a button the config no longer has

        await rig.Monitor.TickAsync();

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Null(rig.History.Refresh(Serial, trend));
    }
}
