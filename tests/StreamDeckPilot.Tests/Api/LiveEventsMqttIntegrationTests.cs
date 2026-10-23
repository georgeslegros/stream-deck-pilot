using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MQTTnet;
using MQTTnet.Protocol;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Tests.Api;

/// <summary>
/// End to end (#108): a value published on MQTT reaches an SSE client within a second, through the
/// real host, MQTT client, bridge and bus, against a real Mosquitto broker.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LiveEventsMqttIntegrationTests : IAsyncLifetime
{
    private const int MqttPort = 1883;

    private readonly IContainer _broker = new ContainerBuilder("eclipse-mosquitto:2")
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(MqttPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
        .Build();

    private StreamDeckApiFactory? _factory;

    public async ValueTask InitializeAsync() => await _broker.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await ((IAsyncDisposable)_factory).DisposeAsync();
        await _broker.DisposeAsync();
    }

    [Fact]
    public async Task AnMqttValue_IsPushedToSse_WithinASecond()
    {
        _factory = new StreamDeckApiFactory(new Dictionary<string, string?>
        {
            ["Mqtt:Host"] = _broker.Hostname,
            ["Mqtt:Port"] = _broker.GetMappedPublicPort(MqttPort).ToString(),
            ["Mqtt:ClientId"] = "live-events-test",
            ["Mqtt:TopicPrefix"] = "sensors/#",
        });
        await _factory.SeedDeviceAsync("SN001");
        var client = _factory.CreateAuthenticatedClient();
        await using var sse = await SseClient.OpenAsync(client, "/api/events");

        var snapshot = await sse.NextAsync("snapshot");
        if (!snapshot.Json.GetProperty("broker").GetProperty("connected").GetBoolean())
            await sse.NextAsync("BrokerOnline", timeout: TimeSpan.FromSeconds(20));

        var button = new ButtonDefinition("co2", 0, "main", new DisplaySpec(Center: new TextZone(null, "{value}")),
            new InboundBinding("sensors/co2", null, null, false, null), [], new Dictionary<string, IReadOnlyList<ButtonAction>>());
        var put = await client.PutAsync("/devices/SN001/config", new StringContent(
            JsonSerializer.Serialize(new DeviceConfig(2, "SN001", [new ButtonGridPage("main", [button])]), JsonOptions.Default),
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        await sse.NextAsync("ConfigChanged");

        using var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_broker.Hostname, _broker.GetMappedPublicPort(MqttPort))
            .WithClientId("live-events-publisher").Build());

        var watch = Stopwatch.StartNew();
        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("sensors/co2").WithPayload("812")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
        var value = await sse.NextAsync("ValueReceived", e => e.Json.GetProperty("topic").GetString() == "sensors/co2");
        watch.Stop();

        Assert.Equal("812", value.Json.GetProperty("value").GetString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"the value took {watch.Elapsed.TotalMilliseconds:0} ms");
        await publisher.DisconnectAsync();
    }
}
