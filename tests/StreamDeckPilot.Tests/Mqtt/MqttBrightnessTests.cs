using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>Set-topic payload handling, driven through the internal seam (no broker needed).</summary>
public sealed class MqttBrightnessTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly BrightnessController _brightness = new(Options.Create(new BrightnessOptions()));
    private readonly MqttClientService _svc;

    public MqttBrightnessTests()
    {
        Directory.CreateDirectory(_storageDir);
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);
        var desired = new DesiredStateStore();
        var supervisor = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(), new CatalogueStore(opts), configStore, desired,
            new ActivePageStore(), new DeviceRenderer(), NullLogger<DeviceSupervisorService>.Instance,
            brightness: _brightness);

        _svc = new MqttClientService(
            Options.Create(new MqttOptions { ClientId = "test" }),
            configStore, desired, new ButtonTopicIndex(), new DeviceRenderer(), supervisor,
            new Infrastructure.Staleness.LastUpdatedStore(),
            NullLogger<MqttClientService>.Instance,
            brightness: _brightness);
    }

    public void Dispose()
    {
        _brightness.Dispose();
        Directory.Delete(_storageDir, recursive: true);
    }

    [Theory]
    [InlineData("25", 25)]
    [InlineData("{\"value\": 60}", 60)]
    [InlineData("140", 100)]
    [InlineData("{\"value\": -10}", 0)]
    public void ValidPayload_IsAppliedClamped(string payload, int expected)
    {
        _svc.HandleBrightnessSet(payload);
        Assert.Equal(expected, _brightness.Effective);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dim")]
    [InlineData("{\"brightness\": 10}")]
    public void GarbagePayload_IsIgnored(string payload)
    {
        _brightness.SetDesired(40);
        _svc.HandleBrightnessSet(payload);
        Assert.Equal(40, _brightness.Effective);
    }

    [Theory]
    [InlineData("home/streamdeck/brightness/set", "home/#", true)]
    [InlineData("office/streamdeck/brightness", "home/#", false)]
    [InlineData("home/streamdeck/brightness/set", "home/+/brightness/set", true)]
    public void SetTopic_ExplicitSubscriptionOnlyWhenOutsideWildcard(string topic, string filter, bool covered) =>
        Assert.Equal(covered, MqttClientService.IsCoveredBy(topic, filter));
}
