using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Controls;
using StreamDeckPilot.Infrastructure.Gestures;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>
/// Setpoint control (#132) end to end against a real Mosquitto broker: key presses on a
/// <see cref="FakeMacroBoard"/> go through the supervisor, the press queue and the gesture detector;
/// the target is published once; the target topic confirms it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SetpointIntegrationTests : IAsyncLifetime
{
    private const int MqttPort = 1883;
    private const string Serial = "SN_SETPOINT";
    // Vertical at anchor 0 on the 5-wide fake deck: + is key 0, value key 5, − key 10.
    private const int Plus = 0, Value = 5, Minus = 10;

    private readonly IContainer _broker = new ContainerBuilder("eclipse-mosquitto:2")
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(MqttPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
        .Build();

    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly List<Func<Task>> _cleanup = [];
    private readonly ConcurrentQueue<(string Topic, string Payload)> _received = new();
    private IMqttClient? _testClient;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_storageDir);
        await _broker.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var step in Enumerable.Reverse(_cleanup)) await step();
        await _broker.DisposeAsync();
        _cts.Dispose();
        if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, recursive: true);
    }

    private static SetpointControl Control(double? longPressStep = null) =>
        new("room", 0, SetpointOrientation.Vertical,
            new SetpointBinding("climate/room/current"),
            new SetpointBinding("climate/room/target"),
            new SetpointCommit("climate/room/set", DelayMs: 400),
            Step: 0.5, Min: 5, Max: 30, Unit: "°", ConfirmTimeoutSeconds: 10, LongPressStep: longPressStep);

    private sealed record Harness(DesiredStateStore Desired, FakeMacroBoard Board);

    private async Task<Harness> StartAsync(SetpointControl control)
    {
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);
        await configStore.SaveAsync(new DeviceConfig(2, Serial, [new ButtonGridPage("main", [], [control])]));

        var board = new FakeMacroBoard { Serial = Serial, Path = "/fake/hid/setpoint" };
        var desired = new DesiredStateStore();
        var setpoints = new SetpointController(desired);
        var supervisor = new DeviceSupervisorService(
            new FakeStreamDeckLibrary([board]), new CatalogueStore(opts), configStore, desired,
            new ActivePageStore(), new DeviceRenderer(), NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60), setpoints: setpoints);
        var service = new MqttClientService(
            Options.Create(new MqttOptions
            {
                Host = _broker.Hostname,
                Port = _broker.GetMappedPublicPort(MqttPort),
                ClientId = "setpoint-test",
                TopicPrefix = "climate/#",
            }),
            configStore, desired, new ButtonTopicIndex(), new DeviceRenderer(), supervisor,
            new Infrastructure.Staleness.LastUpdatedStore(), NullLogger<MqttClientService>.Instance,
            gestureOptions: Options.Create(new GestureOptions { LongPressMs = 300 }), setpoints: setpoints);

        _testClient = new MqttClientFactory().CreateMqttClient();
        _testClient.ApplicationMessageReceivedAsync += e =>
        {
            _received.Enqueue((e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString()));
            return Task.CompletedTask;
        };
        await _testClient.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_broker.Hostname, _broker.GetMappedPublicPort(MqttPort))
            .WithClientId("setpoint-device").Build(), _cts.Token);
        await _testClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("climate/room/set", MqttQualityOfServiceLevel.AtLeastOnce).Build(), _cts.Token);

        await supervisor.StartAsync(_cts.Token);
        await service.StartAsync(_cts.Token);
        _cleanup.Add(async () =>
        {
            await service.StopAsync(CancellationToken.None);
            await supervisor.StopAsync(CancellationToken.None);
            await _testClient.DisconnectAsync();
            service.Dispose();
            _testClient.Dispose();
        });

        Assert.True(await WaitForAsync(() => service.IsConnected && supervisor.GetActivePage(Serial) == "main"),
            "service didn't connect or the board wasn't registered");
        return new Harness(desired, board);
    }

    private Task PublishAsync(string topic, string payload) =>
        _testClient!.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), _cts.Token);

    private static SetpointFace? Face(Harness h, int key) => h.Desired.Get(Serial, "main", key)?.Setpoint;

    private string[] Commands() => [.. _received.Where(m => m.Topic == "climate/room/set").Select(m => m.Payload)];

    private static async Task<bool> WaitForAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(50, CancellationToken.None);
        }
        return true;
    }

    [Fact]
    public async Task SeveralPresses_PublishOneAbsoluteTarget_ThenTheTargetTopicConfirmsIt()
    {
        var h = await StartAsync(Control());
        await PublishAsync("climate/room/current", "20.5");
        await PublishAsync("climate/room/target", "21");
        Assert.True(await WaitForAsync(() => Face(h, Value)?.Target == "21.0°" && Face(h, Value)?.Current == "20.5°"),
            "the value key didn't take the current and target values");
        Assert.Contains(h.Board.RenderCalls, c => c.KeyIndex == Value); // painted on the deck

        h.Board.SimulateKeyPress(Plus);
        h.Board.SimulateKeyRelease(Plus);
        h.Board.SimulateKeyPress(Plus);
        h.Board.SimulateKeyRelease(Plus);
        h.Board.SimulateKeyPress(Plus);
        h.Board.SimulateKeyRelease(Plus);

        Assert.True(await WaitForAsync(() => Face(h, Value) is { Pending: true, Target: "22.5°" }), "the pending target didn't show");
        Assert.True(await WaitForAsync(() => Commands().Length == 1), "nothing was published");
        await Task.Delay(800);
        Assert.Equal(["22.5"], Commands()); // exactly one command, the absolute target

        await PublishAsync("climate/room/target", "22.5");
        Assert.True(await WaitForAsync(() => Face(h, Value) is { Pending: false, Target: "22.5°" }), "the target topic didn't confirm it");
        Assert.Equal(PressFeedback.None, h.Desired.Get(Serial, "main", Value)!.Feedback);
    }

    [Fact]
    public async Task LongPress_OnMinus_MovesByLongPressStep()
    {
        var h = await StartAsync(Control(longPressStep: 2));
        await PublishAsync("climate/room/target", "21");
        Assert.True(await WaitForAsync(() => Face(h, Value)?.Target == "21.0°"));

        h.Board.SimulateKeyPress(Minus);
        Assert.True(await WaitForAsync(() => Face(h, Value)?.Target == "19.0°"), "the long press didn't fire while held");
        h.Board.SimulateKeyRelease(Minus); // the release after a long press does nothing

        Assert.True(await WaitForAsync(() => Commands().Length == 1));
        await Task.Delay(800);
        Assert.Equal(["19"], Commands());
    }
}
