using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenMacroBoard.SDK;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Brightness;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>#26: the broker-offline indicator, its debounce, and the render-time overlay.</summary>
public sealed class BrokerIndicatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public BrokerIndicatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { Thread.Sleep(50); }
        }
    }

    private static readonly KeyBitmap OfflineFallback = KeyBitmap.Create.FromRgb(30, 30, 30);
    private static readonly KeyBitmap Green = KeyBitmap.Create.FromRgb(0x00, 0xAA, 0x00);

    private static ButtonRenderState State(bool dependsOnBroker) =>
        new("b0", "#00AA00", null, IconPlacement.Corner, "42", null, DependsOnBroker: dependsOnBroker);

    // ── Debounce ─────────────────────────────────────────────────────────────

    [Fact]
    public void Disconnect_ShowsOnlyOnceTheDelayHasElapsed()
    {
        var time = new ManualTimeProvider();
        var indicator = new BrokerIndicator(TimeSpan.FromSeconds(3), time);
        var changes = new List<bool>();
        indicator.Changed += changes.Add;

        indicator.ReportDisconnected();
        Assert.False(indicator.ShowOffline);
        Assert.Empty(changes);

        time.FireAll();
        Assert.True(indicator.ShowOffline);
        Assert.Equal([true], changes);
    }

    [Fact]
    public void ReconnectWithinTheDelay_NeverShows()
    {
        var time = new ManualTimeProvider();
        var indicator = new BrokerIndicator(TimeSpan.FromSeconds(3), time);
        var changes = new List<bool>();
        indicator.Changed += changes.Add;

        indicator.ReportDisconnected();
        indicator.ReportConnected();
        time.FireAll();

        Assert.False(indicator.ShowOffline);
        Assert.Empty(changes);
    }

    [Fact]
    public void RepeatedDisconnects_KeepTheFirstDeadline()
    {
        var time = new ManualTimeProvider();
        var indicator = new BrokerIndicator(TimeSpan.FromSeconds(3), time);

        indicator.ReportDisconnected();
        indicator.ReportDisconnected();
        indicator.ReportDisconnected();

        Assert.Single(time.Timers);
    }

    [Fact]
    public void Reconnect_ClearsAndRaisesOnce()
    {
        var time = new ManualTimeProvider();
        var indicator = new BrokerIndicator(TimeSpan.FromSeconds(3), time);
        indicator.ReportDisconnected();
        time.FireAll();
        var changes = new List<bool>();
        indicator.Changed += changes.Add;

        indicator.ReportConnected();
        indicator.ReportConnected();

        Assert.False(indicator.ShowOffline);
        Assert.Equal([false], changes);
    }

    [Fact]
    public void BlipThenOutage_ShowsAfterTheSecondDelay()
    {
        var time = new ManualTimeProvider();
        var indicator = new BrokerIndicator(TimeSpan.FromSeconds(3), time);

        indicator.ReportDisconnected();   // blip…
        indicator.ReportConnected();      // …recovered inside the delay
        indicator.ReportDisconnected();   // real outage
        Assert.False(indicator.ShowOffline);

        time.FireAll();
        Assert.True(indicator.ShowOffline);
    }

    [Fact]
    public void ZeroDelay_ShowsImmediately()
    {
        var indicator = new BrokerIndicator(TimeSpan.Zero, new ManualTimeProvider());

        indicator.ReportDisconnected();

        Assert.True(indicator.ShowOffline);
    }

    // ── Which keys depend on the broker ──────────────────────────────────────

    private static ButtonDefinition Button(InboundBinding? inbound, params ButtonAction[] tap) =>
        new("b0", 0, "main", new DisplaySpec(null, IconPlacement.Corner), inbound, [],
            new Dictionary<string, IReadOnlyList<ButtonAction>> { ["Tap"] = tap });

    [Fact]
    public void BoundAndPublishingKeys_DependOnTheBroker_NavigationKeysDoNot()
    {
        Assert.True(ButtonRenderState.ButtonDependsOnBroker(Button(new InboundBinding("home/t", "value", null, false, null))));
        Assert.True(ButtonRenderState.ButtonDependsOnBroker(Button(null, new PublishAction("home/light/set", "toggle"))));
        Assert.True(ButtonRenderState.ButtonDependsOnBroker(Button(null, new NavigateAction("two"), new PublishAction("home/x", "1"))));
        Assert.False(ButtonRenderState.ButtonDependsOnBroker(Button(null, new NavigateAction("two"))));
        Assert.False(ButtonRenderState.ButtonDependsOnBroker(Button(null)));
    }

    // ── Render-time overlay ──────────────────────────────────────────────────

    [Fact]
    public void Renderer_DrawsBrokerDependentKeysOffline_AndLeavesOthersAlone()
    {
        var indicator = new BrokerIndicator(TimeSpan.Zero);
        indicator.ReportDisconnected();
        var renderer = new DeviceRenderer(indicator);
        var board = new FakeMacroBoard();

        renderer.RenderButton(board, "SN1", 0, State(dependsOnBroker: true));
        renderer.RenderButton(board, "SN1", 1, State(dependsOnBroker: false));

        var calls = board.RenderCalls;
        Assert.Equal(OfflineFallback, calls.Single(c => c.KeyIndex == 0).Bitmap);
        Assert.Equal(Green, calls.Single(c => c.KeyIndex == 1).Bitmap);
    }

    [Fact]
    public void Renderer_DoesNotChangeDesiredState()
    {
        var indicator = new BrokerIndicator(TimeSpan.Zero);
        indicator.ReportDisconnected();
        var store = new DesiredStateStore();
        var state = State(dependsOnBroker: true);
        store.Set("SN1", "main", 0, state);

        new DeviceRenderer(indicator).RenderLatest(new FakeMacroBoard(), "SN1", "main", 0, store);

        Assert.Same(state, store.Get("SN1", "main", 0));
        Assert.False(store.Get("SN1", "main", 0)!.IsOffline);
    }

    // ── Supervisor repaints on change ────────────────────────────────────────

    [Fact]
    public async Task IndicatorChange_RepaintsTheActivePage_AndClearsOnReconnect()
    {
        var time = new ManualTimeProvider();
        var indicator = new BrokerIndicator(TimeSpan.FromSeconds(3), time);
        var opts = Options.Create(new StorageOptions { BaseDirectory = _dir });
        var configStore = new ConfigStore(opts);
        await configStore.SaveAsync(new DeviceConfig(1, "SN1", [
            new ButtonGridPage("main", [
                new ButtonDefinition("t", 0, "main", new DisplaySpec(null, IconPlacement.Corner),
                    new InboundBinding("home/t", "value", null, false, null), [],
                    new Dictionary<string, IReadOnlyList<ButtonAction>>()),
                new ButtonDefinition("nav", 1, "main", new DisplaySpec(null, IconPlacement.Corner), null, [],
                    new Dictionary<string, IReadOnlyList<ButtonAction>> { ["Tap"] = [new NavigateAction("main")] }),
            ])
        ]));
        var desired = new DesiredStateStore();
        var board = new FakeMacroBoard { Serial = "SN1" };
        var supervisor = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(board), new CatalogueStore(opts), configStore, desired, new ActivePageStore(),
            new DeviceRenderer(indicator), NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60), brokerIndicator: indicator);

        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => board.RenderedKeyCount() == board.Keys.Count);
        desired.Set("SN1", "main", 0, State(dependsOnBroker: true)); // a live green value

        board.ClearRenderCalls();
        indicator.ReportDisconnected();
        time.FireAll();

        var offline = board.RenderCalls;
        Assert.Equal(OfflineFallback, offline.Last(c => c.KeyIndex == 0).Bitmap);  // bound key: offline
        Assert.NotEqual(OfflineFallback, offline.Last(c => c.KeyIndex == 1).Bitmap); // navigation key: normal

        board.ClearRenderCalls();
        indicator.ReportConnected();

        Assert.Equal(Green, board.RenderCalls.Last(c => c.KeyIndex == 0).Bitmap);    // value back, undimmed
        Assert.Equal("42", desired.Get("SN1", "main", 0)!.CenterText);

        await supervisor.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(20, CancellationToken.None);
    }
}
