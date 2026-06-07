using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Mqtt.Pipeline;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Infrastructure.StreamDeck;
using StreamDeckPilot.Tests.Supervision;
using Testcontainers.RabbitMq;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>
/// Integration tests that spin up a real RabbitMQ container with the MQTT plugin enabled.
/// Requires Docker to be running.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MqttIntegrationTests : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3-management")
        .WithPortBinding(1883, true)
        .Build();

    private string _storageDir = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_storageDir);

        await _rabbit.StartAsync();
        // Enable MQTT plugin and wait for it to initialise
        await _rabbit.ExecAsync(["rabbitmq-plugins", "enable", "rabbitmq_mqtt"]);
        await Task.Delay(3000); // plugin startup time
    }

    public async ValueTask DisposeAsync()
    {
        await _rabbit.DisposeAsync();
        if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, recursive: true);
    }

    private MqttOptions MqttOpts() => new()
    {
        Host = _rabbit.Hostname,
        Port = _rabbit.GetMappedPublicPort(1883),
        Username = "guest",
        Password = "guest",
        ClientId = "test-client",
    };

    private (MqttClientService Service, DesiredStateStore Store) BuildService(ConfigStore configStore)
    {
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var desiredState = new DesiredStateStore();
        var fakeBoard = new FakeMacroBoard { Serial = "SN_MQTT_TEST" };
        var fakeLib = new FakeStreamDeckLibrary(fakeBoard);
        var supervisor = new DeviceSupervisorService(
            fakeLib,
            new CatalogueStore(opts),
            configStore,
            desiredState,
            new ActivePageStore(),
            new DeviceRenderer(),
            NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60));

        var topicIndex = new ButtonTopicIndex();
        var service = new MqttClientService(
            Options.Create(MqttOpts()),
            configStore,
            desiredState,
            topicIndex,
            new DeviceRenderer(),
            supervisor,
            new Infrastructure.Staleness.LastUpdatedStore(),
            NullLogger<MqttClientService>.Instance);

        return (service, desiredState);
    }

    [Fact]
    public async Task InboundMessage_MatchingRule_UpdatesDesiredState()
    {
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);

        // Seed config: button on topic "home/co2" with rule >800 → red
        var config = new DeviceConfig(1, "SN_MQTT_TEST", [
            new ButtonGridPage("main", [
                new ButtonDefinition("co2", 0, "main",
                    new DisplaySpec(null, IconPlacement.Corner,
                        Center: new TextZone(null, "{value} {unit}"), Bottom: new TextZone("CO2", null)),
                    new InboundBinding("home/co2", "value", "unit", true, null),
                    [new ConditionalRule(">800", "#FF0000", null)],
                    new Dictionary<string, IReadOnlyList<ButtonAction>>())
            ])
        ]);
        await configStore.SaveAsync(config);

        var (svc, store) = BuildService(configStore);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await svc.StartAsync(cts.Token);
        await Task.Delay(1000, CancellationToken.None); // wait for subscription

        // Publish a value that should trigger the red rule
        var publisher = new MqttClientFactory().CreateMqttClient();
        var pubOpts = new MqttClientOptionsBuilder()
            .WithTcpServer(_rabbit.Hostname, _rabbit.GetMappedPublicPort(1883))
            .WithCredentials("guest", "guest")
            .WithClientId("test-publisher")
            .Build();
        await publisher.ConnectAsync(pubOpts, cts.Token);
        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("home/co2")
            .WithPayload("""{"value":1200,"unit":"ppm"}""")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), cts.Token);

        await Task.Delay(1000, CancellationToken.None); // wait for pipeline

        var state = store.Get("SN_MQTT_TEST", "main", 0);
        Assert.NotNull(state);
        Assert.Equal("#FF0000", state.BackgroundColour);
        Assert.Contains("1200", state.CenterText ?? "");

        await svc.StopAsync(CancellationToken.None);
        await publisher.DisconnectAsync();
    }
}
