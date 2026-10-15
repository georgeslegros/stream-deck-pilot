using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;

namespace StreamDeckPilot.Tests.Supervision;

public class DeviceSupervisorTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public DeviceSupervisorTests() => Directory.CreateDirectory(_storageDir);

    public void Dispose()
    {
        // Teardown can race with the supervisor's background work (catalogue writes, and the
        // fire-and-forget connection handlers that StopAsync does not await). A transient file
        // lock under parallel test load must not fail the test, so retry briefly then give up.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(_storageDir, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    // Polls a condition instead of guessing with a fixed delay — fast on an idle machine,
    // tolerant when the CPU is saturated running the whole suite in parallel.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(20, CancellationToken.None);
    }

    private DeviceSupervisorService BuildSupervisor(
        FakeStreamDeckLibrary library, TimeSpan? pollInterval = null, BrightnessController? brightness = null)
    {
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        return new DeviceSupervisorService(
            library,
            new CatalogueStore(opts),
            new ConfigStore(opts),
            new DesiredStateStore(),
            new ActivePageStore(),
            new DeviceRenderer(),
            NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: pollInterval ?? TimeSpan.FromMinutes(60), // disable polling unless asked
            brightness: brightness);
    }

    [Fact]
    public async Task OnStartup_ConnectedDevice_TransitionsToConnected()
    {
        var board = new FakeMacroBoard { Serial = "SN001" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(board));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN001") == DeviceConnectionState.Connected);

        Assert.Equal(DeviceConnectionState.Connected, svc.GetState("SN001"));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnDisconnect_TransitionsToDisconnected()
    {
        var board = new FakeMacroBoard { Serial = "SN002" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(board));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN002") == DeviceConnectionState.Connected);

        board.SimulateDisconnect();
        await WaitUntilAsync(() => svc.GetState("SN002") == DeviceConnectionState.Disconnected);

        Assert.Equal(DeviceConnectionState.Disconnected, svc.GetState("SN002"));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnReconnect_RendersDesiredState()
    {
        var board = new FakeMacroBoard { Serial = "SN003" };
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);

        // Pre-seed a config with 3 buttons on main page
        var config = new DeviceConfig(1, "SN003", [
            new ButtonGridPage("main", [
                new("b0", 0, "main", new(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>()),
                new("b1", 1, "main", new(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>()),
                new("b2", 2, "main", new(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>()),
            ])
        ]);
        await configStore.SaveAsync(config);

        var svc = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(board),
            new CatalogueStore(opts),
            configStore,
            new DesiredStateStore(),
            new ActivePageStore(),
            new DeviceRenderer(),
            NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => board.RenderCallCount() >= 3); // initial render done

        var rendersBefore = board.RenderCallCount();
        board.SimulateDisconnect();
        await Task.Delay(50, CancellationToken.None);

        board.ClearRenderCalls();
        board.SimulateReconnect();
        await WaitUntilAsync(() => board.RenderCallCount() >= 3);

        Assert.True(board.RenderCallCount() >= 3, $"Expected ≥3 render calls on reconnect, got {board.RenderCallCount()}");
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Navigation_BlanksKeysNotBoundOnTargetPage()
    {
        var board = new FakeMacroBoard { Serial = "SN200" };
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);

        var empty = new Dictionary<string, IReadOnlyList<ButtonAction>>();
        // "main" uses keys 0,1,2; "second" uses only key 0.
        var config = new DeviceConfig(1, "SN200", [
            new ButtonGridPage("main", [
                new("m0", 0, "main", new(), null, [], empty),
                new("m1", 1, "main", new(), null, [], empty),
                new("m2", 2, "main", new(), null, [], empty),
            ]),
            new ButtonGridPage("second", [
                new("s0", 0, "second", new(), null, [], empty),
            ]),
        ]);
        await configStore.SaveAsync(config);

        var svc = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(board),
            new CatalogueStore(opts),
            configStore,
            new DesiredStateStore(),
            new ActivePageStore(),
            new DeviceRenderer(),
            NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None); // initial render of "main"

        // Navigate to a page that does not use keys 1 and 2.
        board.ClearRenderCalls();
        svc.SetActivePage("SN200", "second");

        var keysWritten = board.RenderCalls.Select(c => c.KeyIndex).ToHashSet();
        // Every physical key is rewritten so nothing stale survives the page change…
        Assert.Equal(board.Keys.Count, keysWritten.Count);
        // …including the keys that were bound on "main" but are absent on "second".
        Assert.Contains(1, keysWritten);
        Assert.Contains(2, keysWritten);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TwoDevices_FaultOneDoesNotAffectOther()
    {
        var board1 = new FakeMacroBoard { Serial = "SN101", Path = "/fake/0" };
        var board2 = new FakeMacroBoard { Serial = "SN102", Path = "/fake/1" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(board1, board2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN101") == DeviceConnectionState.Connected
                                && svc.GetState("SN102") == DeviceConnectionState.Connected);

        board1.SimulateDisconnect();
        await WaitUntilAsync(() => svc.GetState("SN101") == DeviceConnectionState.Disconnected);

        Assert.Equal(DeviceConnectionState.Disconnected, svc.GetState("SN101"));
        Assert.Equal(DeviceConnectionState.Connected, svc.GetState("SN102"));
        await svc.StopAsync(CancellationToken.None);
    }

    // ── Discovery retries (boot race, not-ready devices, unplug/replug) ────────────

    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(30);

    [Fact]
    public async Task DeviceAppearingAfterStartup_IsDiscoveredByLaterScan()
    {
        // Container started before the kernel created the hidraw node.
        var library = new FakeStreamDeckLibrary();
        var svc = BuildSupervisor(library, FastPoll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => library.EnumerateCalls >= 2);
        Assert.Null(svc.GetBoard("SN400"));

        var board = new FakeMacroBoard { Serial = "SN400" };
        library.Plug(board);
        await WaitUntilAsync(() => svc.GetState("SN400") == DeviceConnectionState.Connected);

        Assert.Equal(DeviceConnectionState.Connected, svc.GetState("SN400"));
        Assert.Same(board, svc.GetBoard("SN400"));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeviceReportingEmptySerial_IsNotRegistered_AndRetried()
    {
        var board = new FakeMacroBoard { Serial = "\0\0" };
        var library = new FakeStreamDeckLibrary(board);
        var svc = BuildSupervisor(library, FastPoll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => library.EnumerateCalls >= 2);

        Assert.Empty(svc.GetAllStates());
        Assert.True(board.IsDisposed);

        board.Serial = "SN401";
        await WaitUntilAsync(() => svc.GetState("SN401") == DeviceConnectionState.Connected);

        Assert.Equal(DeviceConnectionState.Connected, svc.GetState("SN401"));
        Assert.Equal(["SN401"], svc.GetAllStates().Keys);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeviceOpeningDisconnected_IsNotRegistered_AndRetried()
    {
        // StreamDeckSharp hands back a disconnected board when the hidraw node can't be opened yet.
        var board = new FakeMacroBoard { Serial = "SN402", IsConnected = false };
        var library = new FakeStreamDeckLibrary(board);
        var svc = BuildSupervisor(library, FastPoll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => library.EnumerateCalls >= 2);
        Assert.Null(svc.GetBoard("SN402"));

        board.IsConnected = true;
        await WaitUntilAsync(() => svc.GetState("SN402") == DeviceConnectionState.Connected);

        Assert.Equal(DeviceConnectionState.Connected, svc.GetState("SN402"));
        await svc.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("/fake/0")]   // replugged onto the same hidraw node
    [InlineData("/fake/1")]   // re-enumerated onto a different node
    public async Task UnplugThenReplug_ReopensDevice_AndRendersDesiredState(string replugPath)
    {
        var configStore = new ConfigStore(Options.Create(new StorageOptions { BaseDirectory = _storageDir }));
        await configStore.SaveAsync(new DeviceConfig(1, "SN403", [
            new ButtonGridPage("main", [Btn("b0", 0, "main"), Btn("b1", 1, "main")])
        ]));

        var original = new FakeMacroBoard { Serial = "SN403", Path = "/fake/0" };
        var library = new FakeStreamDeckLibrary(original);
        var svc = BuildSupervisor(library, FastPoll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN403") == DeviceConnectionState.Connected);

        library.Unplug(original);
        original.SimulateDisconnect();
        await WaitUntilAsync(() => original.IsDisposed);
        Assert.True(original.IsDisposed);
        Assert.Equal(DeviceConnectionState.Disconnected, svc.GetState("SN403"));
        Assert.Null(svc.GetBoard("SN403"));

        var replugged = new FakeMacroBoard { Serial = "SN403", Path = replugPath };
        library.Plug(replugged);
        // The state turns Connected before the re-render (which loads the config) completes,
        // so wait for the keys themselves rather than the state.
        await WaitUntilAsync(() => replugged.RenderedKeyCount() == replugged.Keys.Count);

        Assert.Equal(DeviceConnectionState.Connected, svc.GetState("SN403"));
        Assert.Same(replugged, svc.GetBoard("SN403"));
        Assert.Equal(replugged.Keys.Count, replugged.RenderedKeyCount());
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReleasedBoard_EventsAreIgnored()
    {
        var original = new FakeMacroBoard { Serial = "SN404", Path = "/fake/0" };
        var library = new FakeStreamDeckLibrary(original);
        var svc = BuildSupervisor(library, FastPoll);
        var presses = 0;
        svc.ButtonPressed += (_, _) => Interlocked.Increment(ref presses);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN404") == DeviceConnectionState.Connected);

        library.Unplug(original);
        original.SimulateDisconnect();
        var replugged = new FakeMacroBoard { Serial = "SN404", Path = "/fake/1" };
        library.Plug(replugged);
        await WaitUntilAsync(() => ReferenceEquals(svc.GetBoard("SN404"), replugged)
                                && svc.GetState("SN404") == DeviceConnectionState.Connected);

        // A late disconnect or key press from the stale handle must not leak into the live device.
        original.SimulateDisconnect();
        original.SimulateKeyPress(0);
        await Task.Delay(100, CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Connected, svc.GetState("SN404"));
        Assert.Equal(0, presses);
        await svc.StopAsync(CancellationToken.None);
    }

    // ── Brightness ───────────────────────────────────────────────────────────────

    private static BrightnessController Brightness(Tests.Brightness.ManualTimeProvider? time = null) =>
        new(Options.Create(new BrightnessOptions()), timeProvider: time);

    [Fact]
    public async Task OnConnect_AppliesCurrentBrightness()
    {
        using var brightness = Brightness();
        brightness.SetDesired(20);
        var board = new FakeMacroBoard { Serial = "SN500" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(board), brightness: brightness);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN500") == DeviceConnectionState.Connected);

        Assert.Equal((byte)20, board.Brightness);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BrightnessChange_AppliedToConnectedBoard_AndReappliedOnReconnect()
    {
        using var brightness = Brightness();
        var board = new FakeMacroBoard { Serial = "SN501" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(board), brightness: brightness);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN501") == DeviceConnectionState.Connected);
        Assert.Equal((byte)80, board.Brightness);

        brightness.SetDesired(35);
        Assert.Equal((byte)35, board.Brightness);

        board.SimulateDisconnect();
        await WaitUntilAsync(() => svc.GetState("SN501") == DeviceConnectionState.Disconnected);
        board.SetBrightness(100); // the deck's own power-on level after a reconnect
        board.SimulateReconnect();
        await WaitUntilAsync(() => board.Brightness == 35);

        Assert.Equal((byte)35, board.Brightness);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task KeyPress_WhileDark_WakesDeck_AndIsSwallowed()
    {
        var time = new Tests.Brightness.ManualTimeProvider();
        using var brightness = Brightness(time);
        brightness.SetDesired(0);
        var board = new FakeMacroBoard { Serial = "SN502" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(board), brightness: brightness);
        var presses = new List<int>();
        svc.ButtonPressed += (_, key) => { lock (presses) presses.Add(key); };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN502") == DeviceConnectionState.Connected);
        Assert.Equal((byte)0, board.Brightness);

        board.SimulateKeyPress(3);   // wakes only
        Assert.Equal((byte)80, board.Brightness);
        Assert.Empty(presses);

        board.SimulateKeyPress(4);   // deck is lit: runs normally
        Assert.Equal([4], presses);

        time.FireAll();              // wake timeout elapses
        Assert.Equal((byte)0, board.Brightness);
        await svc.StopAsync(CancellationToken.None);
    }

    // ── Config-change rebuild (PUT / force-render) ──────────────────────────────

    private static readonly Dictionary<string, IReadOnlyList<ButtonAction>> NoGestures = new();

    private static ButtonDefinition Btn(string id, int key, string page) =>
        new(id, key, page, new DisplaySpec(), null, [], NoGestures);

    [Fact]
    public async Task ApplyConfigChange_RemovedButton_BlanksKeyWithFullRepaint()
    {
        var configStore = new ConfigStore(Options.Create(new StorageOptions { BaseDirectory = _storageDir }));
        // Initial layout uses keys 0,1,2.
        await configStore.SaveAsync(new DeviceConfig(1, "SN300", [
            new ButtonGridPage("main", [Btn("b0", 0, "main"), Btn("b1", 1, "main"), Btn("b2", 2, "main")])
        ]));

        var board = new FakeMacroBoard { Serial = "SN300" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(board));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN300") == DeviceConnectionState.Connected);

        // New layout removes keys 1 and 2.
        await configStore.SaveAsync(new DeviceConfig(1, "SN300", [
            new ButtonGridPage("main", [Btn("b0", 0, "main")])
        ]));

        board.ClearRenderCalls();
        await svc.ApplyConfigChangeAsync("SN300", resetActivePage: true);

        var keysWritten = board.RenderCalls.Select(c => c.KeyIndex).ToHashSet();
        Assert.Equal(board.Keys.Count, keysWritten.Count);   // every key repainted
        Assert.Contains(1, keysWritten);                     // removed buttons' keys blanked
        Assert.Contains(2, keysWritten);
        Assert.Equal("main", svc.GetActivePage("SN300"));

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplyConfigChange_PreserveActivePage_KeepsCurrentButResetsOnRequest()
    {
        var configStore = new ConfigStore(Options.Create(new StorageOptions { BaseDirectory = _storageDir }));
        await configStore.SaveAsync(new DeviceConfig(1, "SN301", [
            new ButtonGridPage("main", [Btn("m0", 0, "main")]),
            new ButtonGridPage("second", [Btn("s0", 0, "second")]),
        ]));

        var board = new FakeMacroBoard { Serial = "SN301" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(board));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN301") == DeviceConnectionState.Connected);

        svc.SetActivePage("SN301", "second");
        Assert.Equal("second", svc.GetActivePage("SN301"));

        await svc.ApplyConfigChangeAsync("SN301", resetActivePage: false);
        Assert.Equal("second", svc.GetActivePage("SN301"));   // preserved

        await svc.ApplyConfigChangeAsync("SN301", resetActivePage: true);
        Assert.Equal("main", svc.GetActivePage("SN301"));      // reset to first

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplyConfigChange_PreserveActivePage_FallsBackWhenPageRemoved()
    {
        var configStore = new ConfigStore(Options.Create(new StorageOptions { BaseDirectory = _storageDir }));
        await configStore.SaveAsync(new DeviceConfig(1, "SN302", [
            new ButtonGridPage("main", [Btn("m0", 0, "main")]),
            new ButtonGridPage("second", [Btn("s0", 0, "second")]),
        ]));

        var board = new FakeMacroBoard { Serial = "SN302" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(board));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN302") == DeviceConnectionState.Connected);

        svc.SetActivePage("SN302", "second");

        // New config no longer has "second" — even with preserve, must fall back to the first page.
        await configStore.SaveAsync(new DeviceConfig(1, "SN302", [
            new ButtonGridPage("main", [Btn("m0", 0, "main")])
        ]));
        await svc.ApplyConfigChangeAsync("SN302", resetActivePage: false);

        Assert.Equal("main", svc.GetActivePage("SN302"));

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_ReleasesEveryBoard_AndIsIdempotent()
    {
        // #69: a stopped service must not leave the deck showing values that no longer update.
        var a = new FakeMacroBoard { Serial = "SN601", Path = "/fake/0" };
        var b = new FakeMacroBoard { Serial = "SN602", Path = "/fake/1" };
        var svc = BuildSupervisor(new FakeStreamDeckLibrary(a, b));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await WaitUntilAsync(() => svc.GetState("SN601") == DeviceConnectionState.Connected
                                   && svc.GetState("SN602") == DeviceConnectionState.Connected);

        await svc.StopAsync(CancellationToken.None);

        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.Null(svc.GetBoard("SN601"));
        Assert.Null(svc.GetBoard("SN602"));
        Assert.Equal(DeviceConnectionState.Disconnected, svc.GetState("SN601"));

        await svc.StopAsync(CancellationToken.None); // second stop: nothing left, no throw
    }

    [Fact]
    public async Task CatalogueWriteFailure_DeckStillWorks_AndEntryIsRetried()
    {
        // #68: a corrupt catalogue used to leave the board registered but never wired (blank,
        // presses ignored) until a restart.
        var catalogPath = Path.Combine(_storageDir, "catalog.json");
        await File.WriteAllTextAsync(catalogPath, "{ not json");
        var configStore = new ConfigStore(Options.Create(new StorageOptions { BaseDirectory = _storageDir }));
        await configStore.SaveAsync(new DeviceConfig(1, "SN501", [
            new ButtonGridPage("main", [Btn("b0", 0, "main")])
        ]));

        var board = new FakeMacroBoard { Serial = "SN501" };
        var library = new FakeStreamDeckLibrary(board);
        var svc = BuildSupervisor(library, FastPoll);
        var presses = 0;
        svc.ButtonPressed += (_, _) => Interlocked.Increment(ref presses);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);

        await WaitUntilAsync(() => board.RenderedKeyCount() == board.Keys.Count);
        Assert.Equal(DeviceConnectionState.Connected, svc.GetState("SN501"));
        Assert.Equal(board.Keys.Count, board.RenderedKeyCount());
        board.SimulateKeyPress(0);
        await WaitUntilAsync(() => Volatile.Read(ref presses) == 1);
        Assert.Equal(1, Volatile.Read(ref presses));

        // Once the file is fixed, a later scan records the device without a restart.
        File.Delete(catalogPath);
        var catalogue = new CatalogueStore(Options.Create(new StorageOptions { BaseDirectory = _storageDir }));
        for (var i = 0; i < 200; i++)
        {
            try { if ((await catalogue.LoadAsync()).Devices.Any(d => d.Serial == "SN501")) break; }
            catch (Exception) { /* mid-write */ }
            await Task.Delay(20);
        }
        Assert.Contains((await catalogue.LoadAsync()).Devices, d => d.Serial == "SN501");

        await svc.StopAsync(CancellationToken.None);
    }
}
