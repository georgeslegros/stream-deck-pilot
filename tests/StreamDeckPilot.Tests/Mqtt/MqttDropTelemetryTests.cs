using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Observability;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>
/// Dropped-message telemetry stays bounded (#63): unbound topics under the wildcard don't each
/// become a metric series, and a bound topic with unusable payloads doesn't warn on every message.
/// </summary>
public sealed class MqttDropTelemetryTests : IDisposable
{
    private const string Serial = "SN_DROPS";
    private const string BoundTopic = "home/office/temp";
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly StreamDeckMetrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, Dictionary<string, object?> Tags)> _measurements = [];
    private readonly ListLogger _logger = new();
    private readonly SettableTimeProvider _time = new();
    private readonly ButtonDefinition _button;
    private readonly MqttClientService _svc;

    public MqttDropTelemetryTests()
    {
        Directory.CreateDirectory(_storageDir);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _metrics.Meter)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            lock (_measurements)
                _measurements.Add((instrument.Name, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        });
        _listener.Start();

        var empty = new Dictionary<string, IReadOnlyList<ButtonAction>>();
        _button = new ButtonDefinition("t0", 0, "main", new DisplaySpec(),
            new InboundBinding(BoundTopic, "value", null, false, null), [], empty);
        var config = new DeviceConfig(1, Serial, [new ButtonGridPage("main", [_button])]);
        var topicIndex = new ButtonTopicIndex();
        topicIndex.Update(Serial, config);

        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);
        var desired = new DesiredStateStore();
        var supervisor = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(new FakeMacroBoard { Serial = Serial }),
            new CatalogueStore(opts),
            configStore,
            desired,
            new ActivePageStore(),
            new DeviceRenderer(),
            NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60));

        _svc = new MqttClientService(
            Options.Create(new MqttOptions { Host = "localhost", Port = 1883, ClientId = "test" }),
            configStore,
            desired,
            topicIndex,
            new DeviceRenderer(),
            supervisor,
            new Infrastructure.Staleness.LastUpdatedStore(),
            _logger,
            _metrics,
            timeProvider: _time);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
        Directory.Delete(_storageDir, recursive: true);
    }

    private List<Dictionary<string, object?>> Dropped()
    {
        lock (_measurements)
            return _measurements.Where(m => m.Instrument == "streamdeck.mqtt.messages_dropped").Select(m => m.Tags).ToList();
    }

    [Fact]
    public async Task UnboundTopic_CountsAsNoBinding_WithoutTopicTag()
    {
        await _svc.ProcessMessageAsync("home/sensor/humidity/attributes", "{}");
        await _svc.ProcessMessageAsync("home/sensor/pressure", "1013");

        var dropped = Dropped();
        Assert.Equal(2, dropped.Count);
        Assert.All(dropped, tags =>
        {
            Assert.Equal("no_binding", tags["reason"]);
            Assert.False(tags.ContainsKey("topic"));
        });
    }

    [Fact]
    public void BoundTopicWithoutValue_KeepsTopicTag()
    {
        _svc.RunPipeline(Serial, "main", _button, "{\"other\":1}");

        var tags = Assert.Single(Dropped());
        Assert.Equal("no_value", tags["reason"]);
        Assert.Equal(BoundTopic, tags["topic"]);
    }

    [Fact]
    public void NoValueWarning_IsRateLimitedPerTopic()
    {
        _svc.RunPipeline(Serial, "main", _button, "{}");
        _svc.RunPipeline(Serial, "main", _button, "{}");
        _svc.RunPipeline(Serial, "main", _button, "{}");
        Assert.Single(_logger.Warnings);
        Assert.Equal(3, Dropped().Count); // every drop is still counted

        _time.Advance(MqttClientService.NoValueWarningInterval);
        _svc.RunPipeline(Serial, "main", _button, "{}");
        Assert.Equal(2, _logger.Warnings.Count);
    }

    private sealed class SettableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ListLogger : ILogger<MqttClientService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
