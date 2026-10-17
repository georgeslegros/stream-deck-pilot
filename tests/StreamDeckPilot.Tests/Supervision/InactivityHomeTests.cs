using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Navigation;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Brightness;

namespace StreamDeckPilot.Tests.Supervision;

/// <summary>Return to the home page after inactivity (#24), wired through the supervisor.</summary>
public class InactivityHomeTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly ManualTimeProvider _time = new();
    private readonly ConfigStore _configStore;
    private readonly IOptions<StorageOptions> _storage;

    public InactivityHomeTests()
    {
        Directory.CreateDirectory(_storageDir);
        _storage = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        _configStore = new ConfigStore(_storage);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(_storageDir, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(20, CancellationToken.None);
    }

    private static ButtonDefinition Button(string id, int key, string page) =>
        new(id, key, page, new(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>());

    private Task SaveConfigAsync(string serial, int? homeAfterInactivitySeconds = null) =>
        _configStore.SaveAsync(new DeviceConfig(2, serial, [
            new ButtonGridPage("home", [Button("a", 0, "home")]),
            new ButtonGridPage("other", [Button("b", 0, "other")]),
        ], homeAfterInactivitySeconds));

    private async Task<(DeviceSupervisorService Svc, FakeMacroBoard Board)> StartAsync(
        string serial, int globalSeconds, BrightnessController? brightness = null)
    {
        var board = new FakeMacroBoard { Serial = serial };
        var inactivity = new InactivityMonitor(
            Options.Create(new NavigationOptions { HomeAfterInactivitySeconds = globalSeconds }), _time);
        var svc = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(board),
            new CatalogueStore(_storage),
            _configStore,
            new DesiredStateStore(),
            new ActivePageStore(),
            new DeviceRenderer(),
            NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60),
            brightness: brightness,
            inactivity: inactivity);

        await svc.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => svc.GetState(serial) == DeviceConnectionState.Connected
                                && svc.GetActivePage(serial) == "home"
                                && board.RenderedKeyCount() == board.Keys.Count);
        return (svc, board);
    }

    [Fact]
    public async Task OffByDefault_StaysOnTheCurrentPage()
    {
        await SaveConfigAsync("SN801");
        var (svc, _) = await StartAsync("SN801", globalSeconds: 0);

        svc.SetActivePage("SN801", "other");
        _time.FireAll();

        Assert.Equal("other", svc.GetActivePage("SN801"));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IdleDeck_ReturnsToTheFirstPage_AndRendersIt()
    {
        await SaveConfigAsync("SN802");
        var (svc, board) = await StartAsync("SN802", globalSeconds: 30);
        svc.SetActivePage("SN802", "other");
        board.ClearRenderCalls();

        _time.FireAll();

        Assert.Equal("home", svc.GetActivePage("SN802"));
        Assert.Equal(board.Keys.Count, board.RenderedKeyCount());
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AlreadyHome_NothingHappens()
    {
        await SaveConfigAsync("SN803");
        var (svc, board) = await StartAsync("SN803", globalSeconds: 30);
        board.ClearRenderCalls();

        _time.FireAll();

        Assert.Equal("home", svc.GetActivePage("SN803"));
        Assert.Equal(0, board.RenderCallCount());
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task KeyPress_RestartsTheTimeout()
    {
        await SaveConfigAsync("SN804");
        var (svc, board) = await StartAsync("SN804", globalSeconds: 30);
        svc.SetActivePage("SN804", "other");
        var beforePress = _time.Timers[^1];

        board.SimulateKeyPress(0);

        Assert.Null(beforePress.DueTime);
        beforePress.FireStale(); // the old timeout firing late must not send the deck home
        Assert.Equal("other", svc.GetActivePage("SN804"));

        _time.FireAll(); // the timeout restarted by the press
        Assert.Equal("home", svc.GetActivePage("SN804"));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PageChange_RestartsTheTimeout()
    {
        await SaveConfigAsync("SN805");
        var (svc, _) = await StartAsync("SN805", globalSeconds: 30);
        svc.SetActivePage("SN805", "other");
        var beforeChange = _time.Timers[^1];

        svc.SetActivePage("SN805", "other"); // API navigate / Navigate action

        Assert.Null(beforeChange.DueTime);
        beforeChange.FireStale();
        Assert.Equal("other", svc.GetActivePage("SN805"));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DarkDeck_GoesHomeWithoutWaking()
    {
        using var brightness = new BrightnessController(Options.Create(new BrightnessOptions()));
        brightness.SetDesired(0);
        await SaveConfigAsync("SN806");
        var (svc, board) = await StartAsync("SN806", globalSeconds: 30, brightness);
        svc.SetActivePage("SN806", "other");

        _time.FireAll();

        Assert.Equal("home", svc.GetActivePage("SN806"));
        Assert.Equal(0, brightness.Effective);
        Assert.Equal((byte)0, board.Brightness);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeviceConfigOverride_TakesPrecedenceOverTheGlobalOption()
    {
        await SaveConfigAsync("SN807", homeAfterInactivitySeconds: 0);
        var (svc, _) = await StartAsync("SN807", globalSeconds: 30);

        svc.SetActivePage("SN807", "other");
        _time.FireAll();

        Assert.Equal("other", svc.GetActivePage("SN807"));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeviceConfigOverride_EnablesItWhenTheGlobalOptionIsOff()
    {
        await SaveConfigAsync("SN808", homeAfterInactivitySeconds: 15);
        var (svc, _) = await StartAsync("SN808", globalSeconds: 0);

        svc.SetActivePage("SN808", "other");
        Assert.Equal(TimeSpan.FromSeconds(15), _time.Timers[^1].DueTime);
        _time.FireAll();

        Assert.Equal("home", svc.GetActivePage("SN808"));
        await svc.StopAsync(CancellationToken.None);
    }
}
