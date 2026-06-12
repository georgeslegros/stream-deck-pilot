using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Models.Config;
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

    private DeviceSupervisorService BuildSupervisor(FakeStreamDeckLibrary library)
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
            pollInterval: TimeSpan.FromMinutes(60)); // disable polling in tests
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
        await Task.Delay(200, CancellationToken.None);

        var rendersBefore = board.RenderCalls.Count;
        board.SimulateDisconnect();
        await Task.Delay(50, CancellationToken.None);

        board.RenderCalls.Clear();
        board.SimulateReconnect();
        await Task.Delay(200, CancellationToken.None);

        Assert.True(board.RenderCalls.Count >= 3, $"Expected ≥3 render calls on reconnect, got {board.RenderCalls.Count}");
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
        board.RenderCalls.Clear();
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

        board.RenderCalls.Clear();
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
}
