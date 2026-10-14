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
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>
/// Integration tests against a real Mosquitto broker (the production broker) in a container.
/// Requires Docker to be running.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MqttIntegrationTests : IAsyncLifetime
{
    private const int MqttPort = 1883;

    // The image ships /mosquitto-no-auth.conf: a listener on 1883 with anonymous access.
    private readonly IContainer _broker = new ContainerBuilder("eclipse-mosquitto:2")
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(MqttPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
        .Build();

    private string _storageDir = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_storageDir);
        await _broker.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, recursive: true);
    }

    private MqttOptions MqttOpts() => new()
    {
        Host = _broker.Hostname,
        Port = _broker.GetMappedPublicPort(MqttPort),
        ClientId = "test-client",
    };

    private async Task<IMqttClient> ConnectPublisherAsync(CancellationToken ct)
    {
        var publisher = new MqttClientFactory().CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_broker.Hostname, _broker.GetMappedPublicPort(MqttPort))
            .WithClientId("test-publisher")
            .Build();
        var connect = await publisher.ConnectAsync(options, ct);
        Assert.Equal(MqttClientConnectResultCode.Success, connect.ResultCode);
        return publisher;
    }

    private static Task PublishAsync(IMqttClient publisher, string payload, bool retain, CancellationToken ct) =>
        publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("home/co2")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build(), ct);

    // Polls instead of sleeping a guessed amount: connect + subscribe happen in the background.
    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100, CancellationToken.None);
        }
        return true;
    }

    private static async Task<ConfigStore> SeedCo2ConfigAsync(IOptions<StorageOptions> opts)
    {
        var configStore = new ConfigStore(opts);
        // Button on topic "home/co2" with rule >800 → red, else green.
        await configStore.SaveAsync(new DeviceConfig(1, "SN_MQTT_TEST", [
            new ButtonGridPage("main", [
                new ButtonDefinition("co2", 0, "main",
                    new DisplaySpec(null, IconPlacement.Corner,
                        Center: new TextZone(null, "{value} {unit}"), Bottom: new TextZone("CO2", null)),
                    new InboundBinding("home/co2", "value", "unit", true, null),
                    [new ConditionalRule(">800", "#FF0000", null), new ConditionalRule("any", "#00AA00", null)],
                    new Dictionary<string, IReadOnlyList<ButtonAction>>())
            ])
        ]));
        return configStore;
    }

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

    // A value retained before the service subscribes reaches it through the home/# wildcard.
    // (RabbitMQ's MQTT plugin does not do this, which hid the class of bug these tests target.)
    [Fact]
    public async Task RetainedBeforeSubscribe_ReachesWildcardSubscription()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var publisher = await ConnectPublisherAsync(cts.Token);
        await PublishAsync(publisher, """{"value":1200,"unit":"ppm"}""", retain: true, cts.Token);

        var configStore = await SeedCo2ConfigAsync(Options.Create(new StorageOptions { BaseDirectory = _storageDir }));
        var (svc, store) = BuildService(configStore);
        await svc.StartAsync(cts.Token);

        Assert.True(await WaitForAsync(() => store.Get("SN_MQTT_TEST", "main", 0) is not null, TimeSpan.FromSeconds(15)),
            "retained value was not delivered to the wildcard subscription");
        var state = store.Get("SN_MQTT_TEST", "main", 0)!;
        Assert.Equal("#FF0000", state.BackgroundColour);
        Assert.Contains("1200", state.CenterText ?? "");

        await svc.StopAsync(CancellationToken.None);
        await publisher.DisconnectAsync();
    }

    [Fact]
    public async Task LiveMessage_MatchingRule_UpdatesDesiredState()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var publisher = await ConnectPublisherAsync(cts.Token);
        // The retained value doubles as the "subscribed" signal, so the live publish below
        // cannot race the service's background subscribe.
        await PublishAsync(publisher, """{"value":1200,"unit":"ppm"}""", retain: true, cts.Token);

        var configStore = await SeedCo2ConfigAsync(Options.Create(new StorageOptions { BaseDirectory = _storageDir }));
        var (svc, store) = BuildService(configStore);
        await svc.StartAsync(cts.Token);
        Assert.True(await WaitForAsync(() => store.Get("SN_MQTT_TEST", "main", 0) is not null, TimeSpan.FromSeconds(15)));

        await PublishAsync(publisher, """{"value":450,"unit":"ppm"}""", retain: false, cts.Token);

        Assert.True(await WaitForAsync(() => store.Get("SN_MQTT_TEST", "main", 0)?.BackgroundColour == "#00AA00", TimeSpan.FromSeconds(10)),
            "live message did not update the button");
        Assert.Contains("450", store.Get("SN_MQTT_TEST", "main", 0)!.CenterText ?? "");

        await svc.StopAsync(CancellationToken.None);
        await publisher.DisconnectAsync();
    }
}
