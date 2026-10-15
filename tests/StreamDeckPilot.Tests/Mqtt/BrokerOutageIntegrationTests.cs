using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using OpenMacroBoard.SDK;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Mqtt.Pipeline;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>
/// #26 end to end against a real Mosquitto: stopping the broker shows the offline look on the
/// bound key, and starting it again clears it with the retained value delivered and painted.
/// Requires Docker.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BrokerOutageIntegrationTests : IAsyncLifetime
{
    private const int MqttPort = 1883;
    private const string Serial = "SN_OUTAGE_TEST";

    // Persistence on, so retained messages survive the restart like on a production broker.
    private const string MosquittoConf = """
        listener 1883
        allow_anonymous true
        persistence true
        persistence_location /mosquitto/data/
        """;

    // A fixed host port: a randomly mapped one changes when the container restarts, and the
    // service under test keeps the address it was given.
    private readonly int _hostPort = FreePort();
    private readonly IContainer _broker;
    private string _storageDir = string.Empty;

    private static readonly KeyBitmap OfflineFallback = KeyBitmap.Create.FromRgb(30, 30, 30);
    private static readonly KeyBitmap Red = KeyBitmap.Create.FromRgb(0xFF, 0x00, 0x00);

    public BrokerOutageIntegrationTests()
    {
        _broker = new ContainerBuilder("eclipse-mosquitto:2")
            .WithResourceMapping(Encoding.UTF8.GetBytes(MosquittoConf), "/mosquitto/config/test.conf")
            .WithCommand("mosquitto", "-c", "/mosquitto/config/test.conf")
            .WithPortBinding(_hostPort, MqttPort)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
            .Build();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask InitializeAsync()
    {
        _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_storageDir);
        await _broker.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(_storageDir, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { await Task.Delay(50); }
        }
    }

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

    private static KeyBitmap? LastPaint(FakeMacroBoard board, int key) =>
        board.RenderCalls.LastOrDefault(c => c.KeyIndex == key).Bitmap;

    [Fact]
    public async Task BrokerStops_KeyShowsOffline_BrokerReturns_RetainedValueRepainted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        // A retained value the broker must resend after its restart.
        var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_broker.Hostname, _hostPort).WithClientId("outage-publisher").Build(), cts.Token);
        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("home/co2").WithPayload("""{"value":1200}""")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag().Build(), cts.Token);
        await publisher.DisconnectAsync();

        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);
        await configStore.SaveAsync(new DeviceConfig(1, Serial, [
            new ButtonGridPage("main", [
                new ButtonDefinition("co2", 0, "main", new DisplaySpec(Center: new TextZone(null, "{value}")),
                    new InboundBinding("home/co2", "value", null, true, null),
                    [new ConditionalRule(">800", "#FF0000", null)],
                    new Dictionary<string, IReadOnlyList<ButtonAction>>())
            ])
        ]));

        var indicator = new BrokerIndicator(TimeSpan.FromSeconds(1));
        var renderer = new DeviceRenderer(indicator);
        var desired = new DesiredStateStore();
        var board = new FakeMacroBoard { Serial = Serial };
        var supervisor = new DeviceSupervisorService(
            new FakeStreamDeckLibrary(board), new CatalogueStore(opts), configStore, desired, new ActivePageStore(),
            renderer, NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60), brokerIndicator: indicator);
        var service = new MqttClientService(
            Options.Create(new MqttOptions
            {
                Host = _broker.Hostname, Port = _hostPort, ClientId = "outage-test", MaxReconnectDelaySeconds = 2,
            }),
            configStore, desired, new ButtonTopicIndex(), renderer, supervisor,
            new Infrastructure.Staleness.LastUpdatedStore(), NullLogger<MqttClientService>.Instance,
            brokerIndicator: indicator);

        await supervisor.StartAsync(cts.Token);
        await service.StartAsync(cts.Token);

        // Connected: the retained value is painted normally.
        Assert.True(await WaitForAsync(() => desired.Get(Serial, "main", 0)?.BackgroundColour == "#FF0000"
            && Red.Equals(LastPaint(board, 0)), TimeSpan.FromSeconds(20)), "retained value never painted");
        var before = desired.Get(Serial, "main", 0);
        Assert.False(indicator.ShowOffline);

        // Broker gone: after the delay the bound key is drawn offline; its value is kept.
        await _broker.StopAsync(cts.Token);
        Assert.True(await WaitForAsync(() => indicator.ShowOffline && OfflineFallback.Equals(LastPaint(board, 0)),
            TimeSpan.FromSeconds(30)), "offline indicator not shown");
        Assert.Same(before, desired.Get(Serial, "main", 0));

        // Broker back: the indicator clears and the retained value arrives again and is painted.
        await _broker.StartAsync(cts.Token);
        Assert.True(await WaitForAsync(() => !indicator.ShowOffline
            && !ReferenceEquals(before, desired.Get(Serial, "main", 0))
            && Red.Equals(LastPaint(board, 0)), TimeSpan.FromSeconds(45)),
            "indicator not cleared, or the retained value was not redelivered and painted");

        await service.StopAsync(CancellationToken.None);
        await supervisor.StopAsync(CancellationToken.None);
    }
}
