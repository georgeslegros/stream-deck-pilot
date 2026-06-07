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
    public void Dispose() => Directory.Delete(_storageDir, recursive: true);

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
        await Task.Delay(200, CancellationToken.None); // let the scan complete

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
        await Task.Delay(200, CancellationToken.None);

        board.SimulateDisconnect();
        await Task.Delay(100, CancellationToken.None);

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
        await Task.Delay(200, CancellationToken.None);

        board1.SimulateDisconnect();
        await Task.Delay(100, CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Disconnected, svc.GetState("SN101"));
        Assert.Equal(DeviceConnectionState.Connected, svc.GetState("SN102"));
        await svc.StopAsync(CancellationToken.None);
    }
}
