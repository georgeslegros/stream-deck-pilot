using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>
/// Fast (no-Docker) regression tests for the cross-page render guard in
/// <see cref="MqttClientService.RunPipeline"/>: an inbound update for a button that lives on a
/// page which is NOT the device's active page must update desired state but must NOT paint the
/// physical board (otherwise an off-page tile bleeds over the visible page — the "page 2 items
/// appear on page 1" bug). These drive the pipeline directly via the internal RunPipeline seam,
/// so no MQTT broker / container is required.
/// </summary>
public sealed class MqttCrossPageRenderTests : IDisposable
{
    private const string Serial = "SN_XPAGE";
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public MqttCrossPageRenderTests() => Directory.CreateDirectory(_storageDir);
    public void Dispose() => Directory.Delete(_storageDir, recursive: true);

    // Two pages: "main" (active) with button m0 on key 0, and "climate" (inactive) with button
    // c0 on key 0 bound to "home/climate/temp". Returns the wired-up service + collaborators.
    private async Task<(MqttClientService Svc, FakeMacroBoard Board, DesiredStateStore Desired)>
        BuildAsync(string activePage)
    {
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);

        var empty = new Dictionary<string, IReadOnlyList<ButtonAction>>();
        var config = new DeviceConfig(1, Serial, [
            new ButtonGridPage("main", [
                new("m0", 0, "main", new(), null, [], empty),
            ]),
            new ButtonGridPage("climate", [
                new("c0", 0, "climate",
                    new DisplaySpec(null, IconPlacement.Corner,
                        Center: new TextZone(null, "{value} {unit}"), Bottom: new TextZone("Temp", null)),
                    new InboundBinding("home/climate/temp", "value", "unit", true, null),
                    [], empty),
            ]),
        ]);
        await configStore.SaveAsync(config);

        var desired = new DesiredStateStore();
        var board = new FakeMacroBoard { Serial = Serial };
        var supervisor = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(board),
            new CatalogueStore(opts),
            configStore,
            desired,
            new ActivePageStore(),
            new DeviceRenderer(),
            NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60));

        // Bring the device up so the supervisor holds the board and seeds the active page,
        // then pin the active page to the one this test wants.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await supervisor.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None);
        supervisor.SetActivePage(Serial, activePage);

        var topicIndex = new ButtonTopicIndex();
        topicIndex.Update(Serial, config);

        var svc = new MqttClientService(
            Options.Create(new MqttOptions { Host = "localhost", Port = 1883, ClientId = "test" }),
            configStore,
            desired,
            topicIndex,
            new DeviceRenderer(),
            supervisor,
            new Infrastructure.Staleness.LastUpdatedStore(),
            NullLogger<MqttClientService>.Instance);

        return (svc, board, desired);
    }

    [Fact]
    public async Task InboundForButtonOnInactivePage_UpdatesDesiredState_ButDoesNotPaintBoard()
    {
        var (svc, board, desired) = await BuildAsync(activePage: "main");
        board.RenderCalls.Clear(); // ignore the initial render of "main"

        // Drive an update for c0, which lives on the INACTIVE "climate" page.
        svc.RunPipeline(Serial, "climate", ButtonOn("climate"), """{"value":21.5,"unit":"°C"}""");

        // Desired state for (climate, key 0) is updated regardless of active page…
        var state = desired.Get(Serial, "climate", 0);
        Assert.NotNull(state);
        Assert.Contains("21.5", state.CenterText ?? "");

        // …but NOTHING was painted to the board, so it cannot bleed over the visible "main" page.
        Assert.Empty(board.RenderCalls);
    }

    [Fact]
    public async Task InboundForButtonOnActivePage_PaintsBoard()
    {
        // Same button, but now "climate" is the active page — it must paint.
        var (svc, board, desired) = await BuildAsync(activePage: "climate");
        board.RenderCalls.Clear();

        svc.RunPipeline(Serial, "climate", ButtonOn("climate"), """{"value":21.5,"unit":"°C"}""");

        Assert.NotNull(desired.Get(Serial, "climate", 0));
        Assert.Contains(board.RenderCalls, c => c.KeyIndex == 0);
    }

    private static ButtonDefinition ButtonOn(string pageId) =>
        new("c0", 0, pageId,
            new DisplaySpec(null, IconPlacement.Corner,
                Center: new TextZone(null, "{value} {unit}"), Bottom: new TextZone("Temp", null)),
            new InboundBinding("home/climate/temp", "value", "unit", true, null),
            [], new Dictionary<string, IReadOnlyList<ButtonAction>>());
}
