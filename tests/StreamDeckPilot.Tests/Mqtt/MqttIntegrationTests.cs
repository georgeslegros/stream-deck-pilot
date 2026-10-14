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

    private static Task PublishAsync(IMqttClient publisher, string payload, bool retain, CancellationToken ct,
        string topic = "home/co2") =>
        publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
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

    private (MqttClientService Service, DesiredStateStore Store, DeviceSupervisorService Supervisor) BuildService(ConfigStore configStore)
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

        return (service, desiredState, supervisor);
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
        var (svc, store, _) = BuildService(configStore);
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
        var (svc, store, _) = BuildService(configStore);
        await svc.StartAsync(cts.Token);
        Assert.True(await WaitForAsync(() => store.Get("SN_MQTT_TEST", "main", 0) is not null, TimeSpan.FromSeconds(15)));

        await PublishAsync(publisher, """{"value":450,"unit":"ppm"}""", retain: false, cts.Token);

        Assert.True(await WaitForAsync(() => store.Get("SN_MQTT_TEST", "main", 0)?.BackgroundColour == "#00AA00", TimeSpan.FromSeconds(10)),
            "live message did not update the button");
        Assert.Contains("450", store.Get("SN_MQTT_TEST", "main", 0)!.CenterText ?? "");

        await svc.StopAsync(CancellationToken.None);
        await publisher.DisconnectAsync();
    }

    // #41: a config change rebuilds the device's desired state, and the wildcard subscription
    // only received retained messages when it was made. Both a newly bound topic and a topic
    // that was already bound must show their retained value again without a new publish.
    [Fact]
    public async Task ConfigChange_TilesGetRetainedValuesWithoutNewPublish()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var publisher = await ConnectPublisherAsync(cts.Token);
        await PublishAsync(publisher, """{"value":1200,"unit":"ppm"}""", retain: true, cts.Token);
        await PublishAsync(publisher, "48.7", retain: true, cts.Token, topic: "home/humidity");

        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = await SeedCo2ConfigAsync(opts);
        var (svc, store, supervisor) = BuildService(configStore);
        await svc.StartAsync(cts.Token);
        Assert.True(await WaitForAsync(() => store.Get("SN_MQTT_TEST", "main", 0) is not null, TimeSpan.FromSeconds(15)));

        // PUT: keep the CO2 button, add a humidity button on key 1. Same sequence as the endpoint.
        var config = (await configStore.LoadAsync("SN_MQTT_TEST"))!;
        var grid = (ButtonGridPage)config.Pages[0];
        var humidity = new ButtonDefinition("humidity", 1, "main",
            new DisplaySpec(Center: new TextZone(null, "{value} %"), Precision: 0),
            new InboundBinding("home/humidity", null, null, true, null),
            [], new Dictionary<string, IReadOnlyList<ButtonAction>>());
        await configStore.SaveAsync(config with { Pages = [grid with { Buttons = [.. grid.Buttons, humidity] }] });
        await svc.NotifyConfigChangedAsync("SN_MQTT_TEST", cts.Token);
        await supervisor.ApplyConfigChangeAsync("SN_MQTT_TEST", resetActivePage: false);
        await svc.RefreshRetainedAsync("SN_MQTT_TEST", cts.Token);

        Assert.True(await WaitForAsync(() => store.Get("SN_MQTT_TEST", "main", 1)?.CenterText == "49 %", TimeSpan.FromSeconds(10)),
            $"newly bound tile did not get its retained value (got '{store.Get("SN_MQTT_TEST", "main", 1)?.CenterText}')");
        Assert.True(await WaitForAsync(() => store.Get("SN_MQTT_TEST", "main", 0)?.BackgroundColour == "#FF0000", TimeSpan.FromSeconds(10)),
            "already bound tile lost its value after the config change");
        Assert.Contains("1200", store.Get("SN_MQTT_TEST", "main", 0)!.CenterText ?? "");

        // Unsubscribing the exact topics must leave the wildcard in place: live updates still arrive.
        await PublishAsync(publisher, "51.2", retain: false, cts.Token, topic: "home/humidity");
        Assert.True(await WaitForAsync(() => store.Get("SN_MQTT_TEST", "main", 1)?.CenterText == "51 %", TimeSpan.FromSeconds(10)),
            "live update did not arrive after the retained refresh");

        await svc.StopAsync(CancellationToken.None);
        await publisher.DisconnectAsync();
    }
}
