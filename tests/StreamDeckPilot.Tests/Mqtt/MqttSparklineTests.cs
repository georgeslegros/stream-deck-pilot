using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Sparkline;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>
/// The inbound pipeline attaches a sparkline (#25) only to buttons that opt in, and the trend
/// follows the values that arrive (driven through the RunPipeline seam, no broker needed).
/// </summary>
public sealed class MqttSparklineTests : IDisposable
{
    private const string Serial = "SN_TREND";
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly Clock _clock = new();

    public MqttSparklineTests() => Directory.CreateDirectory(_storageDir);
    public void Dispose() => Directory.Delete(_storageDir, recursive: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly Dictionary<string, IReadOnlyList<ButtonAction>> NoGestures = new();

    private static readonly ButtonDefinition Trend = new("trend", 0, "main",
        new DisplaySpec(Center: new TextZone(null, "{value}"), Sparkline: new SparklineSpec(6)),
        new InboundBinding("sensors/co2", null, null, false, null), [], NoGestures);

    private static readonly ButtonDefinition Plain = new("plain", 1, "main",
        new DisplaySpec(Center: new TextZone(null, "{value}")),
        new InboundBinding("sensors/co2", null, null, false, null), [], NoGestures);

    private async Task<(MqttClientService Svc, FakeMacroBoard Board, DesiredStateStore Desired)> BuildAsync()
    {
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);
        var config = new DeviceConfig(1, Serial, [new ButtonGridPage("main", [Trend, Plain])]);
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

        var topicIndex = new ButtonTopicIndex();
        topicIndex.Update(Serial, config);
        var svc = new MqttClientService(
            Options.Create(new MqttOptions { Host = "localhost", Port = 1883, ClientId = "test" }),
            configStore, desired, topicIndex, new DeviceRenderer(), supervisor,
            new Infrastructure.Staleness.LastUpdatedStore(), NullLogger<MqttClientService>.Instance,
            sparklines: new SparklineHistory(Options.Create(new SparklineOptions()), _clock));
        return (svc, board, desired);
    }

    [Fact]
    public async Task OptedInButton_GetsATrendThatFollowsTheValues()
    {
        var (svc, board, desired) = await BuildAsync();
        board.ClearRenderCalls();

        svc.RunPipeline(Serial, "main", Trend, "400");
        _clock.Now = _clock.Now.AddHours(3);
        svc.RunPipeline(Serial, "main", Trend, "900");

        var state = desired.Get(Serial, "main", 0)!;
        Assert.Equal("900", state.CenterText);
        Assert.NotNull(state.Sparkline);
        Assert.Equal([400.0, 900.0], state.Sparkline.Points.Select(p => p.Value));
        Assert.Equal((400, 900), (state.Sparkline.Min, state.Sparkline.Max));
        Assert.Contains(board.RenderCalls, c => c.KeyIndex == 0);
    }

    [Fact]
    public async Task ButtonWithoutSparkline_HasNoTrend()
    {
        var (svc, _, desired) = await BuildAsync();

        svc.RunPipeline(Serial, "main", Plain, "400");

        Assert.Null(desired.Get(Serial, "main", 1)!.Sparkline);
    }
}
