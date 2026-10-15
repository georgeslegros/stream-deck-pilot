using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Tests.Supervision;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>
/// Key press → gesture → actions, end to end: a <see cref="FakeMacroBoard"/> key press goes through
/// the supervisor and the MQTT service to a real Mosquitto broker (#75), in press order (#74).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ButtonPressTests : IAsyncLifetime
{
    private const int MqttPort = 1883;
    private const string SerialA = "SN_PRESS_A";
    private const string SerialB = "SN_PRESS_B";

    private readonly IContainer _broker = new ContainerBuilder("eclipse-mosquitto:2")
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(MqttPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
        .Build();

    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly List<Func<Task>> _cleanup = [];

    // Everything the subscriber saw under cmd/#, in arrival order.
    private readonly ConcurrentQueue<(string Topic, string Payload, MqttQualityOfServiceLevel Qos, bool Retain)> _received = new();

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

    private static ButtonDefinition Key(int keyIndex, string pageId, params ButtonAction[] tap) =>
        new($"k{keyIndex}-{pageId}", keyIndex, pageId,
            new DisplaySpec(null, IconPlacement.Corner, Center: new TextZone($"K{keyIndex}", null)),
            null, [],
            new Dictionary<string, IReadOnlyList<ButtonAction>> { ["Tap"] = tap });

    private sealed record Harness(
        MqttClientService Service, DeviceSupervisorService Supervisor, Dictionary<string, FakeMacroBoard> Boards);

    private async Task<Harness> StartAsync(
        IReadOnlyList<ButtonGridPage> pages, BrightnessController? brightness = null, bool startService = true,
        params string[] serials)
    {
        if (serials.Length == 0) serials = [SerialA];
        var opts = Options.Create(new StorageOptions { BaseDirectory = _storageDir });
        var configStore = new ConfigStore(opts);
        foreach (var serial in serials)
            await configStore.SaveAsync(new DeviceConfig(1, serial, pages));

        var boards = serials.ToDictionary(s => s, s => new FakeMacroBoard { Serial = s, Path = $"/fake/hid/{s}" });
        var desired = new DesiredStateStore();
        var supervisor = new DeviceSupervisorService(
            new FakeStreamDeckLibrary([.. boards.Values]), new CatalogueStore(opts), configStore, desired,
            new ActivePageStore(), new DeviceRenderer(), NullLogger<DeviceSupervisorService>.Instance,
            pollInterval: TimeSpan.FromMinutes(60), brightness: brightness);
        var service = new MqttClientService(
            Options.Create(new MqttOptions
            {
                Host = _broker.Hostname,
                Port = _broker.GetMappedPublicPort(MqttPort),
                ClientId = "press-test",
            }),
            configStore, desired, new ButtonTopicIndex(), new DeviceRenderer(), supervisor,
            new Infrastructure.Staleness.LastUpdatedStore(), NullLogger<MqttClientService>.Instance,
            brightness: brightness);

        var subscriber = new MqttClientFactory().CreateMqttClient();
        subscriber.ApplicationMessageReceivedAsync += e =>
        {
            var m = e.ApplicationMessage;
            _received.Enqueue((m.Topic, m.ConvertPayloadToString(), m.QualityOfServiceLevel, m.Retain));
            return Task.CompletedTask;
        };
        await subscriber.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_broker.Hostname, _broker.GetMappedPublicPort(MqttPort))
            .WithClientId("press-subscriber").Build(), _cts.Token);
        await subscriber.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter("cmd/#", MqttQualityOfServiceLevel.AtLeastOnce).Build(), _cts.Token);

        await supervisor.StartAsync(_cts.Token);
        if (startService) await service.StartAsync(_cts.Token);
        _cleanup.Add(async () =>
        {
            await service.StopAsync(CancellationToken.None);
            await supervisor.StopAsync(CancellationToken.None);
            await subscriber.DisconnectAsync();
            service.Dispose();
            subscriber.Dispose();
        });

        Assert.True(await WaitForAsync(() =>
                (service.IsConnected || !startService) && serials.All(s => supervisor.GetActivePage(s) == pages[0].PageId)),
            "service didn't connect or the boards weren't registered");
        return new Harness(service, supervisor, boards);
    }

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

    private string[] Payloads(string topic) =>
        [.. _received.Where(m => m.Topic == topic).Select(m => m.Payload)];

    [Fact]
    public async Task Press_PublishesConfiguredTopicAndPayload_AtQos1_NotRetained()
    {
        var h = await StartAsync([new ButtonGridPage("main", [Key(0, "main", new PublishAction("cmd/light", "ON"))])]);

        h.Boards[SerialA].SimulateKeyPress(0);

        Assert.True(await WaitForAsync(() => !_received.IsEmpty), "nothing was published");
        var (topic, payload, qos, retain) = Assert.Single(_received);
        Assert.Equal("cmd/light", topic);
        Assert.Equal("ON", payload);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, qos);
        Assert.False(retain);
    }

    [Fact]
    public async Task Press_OnKeyWithNoButton_DoesNothing_AndLaterPressesStillWork()
    {
        var h = await StartAsync([new ButtonGridPage("main", [Key(1, "main", new PublishAction("cmd/one", "1"))])]);

        h.Boards[SerialA].SimulateKeyPress(0); // no button on key 0
        h.Boards[SerialA].SimulateKeyPress(1);

        Assert.True(await WaitForAsync(() => Payloads("cmd/one").Length == 1));
        Assert.Single(_received);
    }

    [Fact]
    public async Task Press_PublishThenNavigate_RunsBothInOrder_AndRendersTheNewPage()
    {
        var h = await StartAsync([
            new ButtonGridPage("main", [Key(0, "main", new PublishAction("cmd/go", "two"), new NavigateAction("two"))]),
            new ButtonGridPage("two", [Key(3, "two")]),
        ]);
        var board = h.Boards[SerialA];
        board.ClearRenderCalls();

        board.SimulateKeyPress(0);

        Assert.True(await WaitForAsync(() => h.Supervisor.GetActivePage(SerialA) == "two"), "didn't navigate");
        Assert.True(await WaitForAsync(() => Payloads("cmd/go").Length == 1), "didn't publish");
        Assert.Equal(board.Keys.Count, board.RenderedKeyCount()); // the whole new page was painted
    }

    [Fact]
    public async Task Press_FailingFirstAction_StillRunsTheRest()
    {
        // A wildcard topic can't be published to: the client rejects it, and the Navigate after it must still run.
        var h = await StartAsync([
            new ButtonGridPage("main", [Key(0, "main", new PublishAction("cmd/#", "x"), new NavigateAction("two"))]),
            new ButtonGridPage("two", [Key(1, "two", new PublishAction("cmd/after", "ok"))]),
        ]);

        h.Boards[SerialA].SimulateKeyPress(0);
        Assert.True(await WaitForAsync(() => h.Supervisor.GetActivePage(SerialA) == "two"), "the Navigate after the failure didn't run");

        h.Boards[SerialA].SimulateKeyPress(1);
        Assert.True(await WaitForAsync(() => Payloads("cmd/after").Length == 1), "the press queue stopped after a failure");
    }

    [Fact]
    public async Task QuickPresses_PublishInPressOrder()
    {
        var h = await StartAsync([new ButtonGridPage("main", [
            Key(0, "main", new PublishAction("cmd/seq", "on")),
            Key(1, "main", new PublishAction("cmd/seq", "off")),
        ])]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.BeforePressHandled = _ => gate.Task;

        // All presses are queued before any runs, so they would all race if handled independently.
        string[] expected = [.. Enumerable.Range(0, 20).Select(i => i % 3 == 0 ? "on" : "off")];
        foreach (var payload in expected) h.Boards[SerialA].SimulateKeyPress(payload == "on" ? 0 : 1);
        gate.SetResult();

        Assert.True(await WaitForAsync(() => Payloads("cmd/seq").Length == expected.Length), "not every press published");
        Assert.Equal(expected, Payloads("cmd/seq"));
    }

    [Fact]
    public async Task Press_RunsAgainstThePageOnScreenWhenTheKeyWentDown()
    {
        var h = await StartAsync([
            new ButtonGridPage("main", [Key(1, "main", new PublishAction("cmd/page", "main"))]),
            new ButtonGridPage("two", [Key(1, "two", new PublishAction("cmd/page", "two"))]),
        ]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.BeforePressHandled = _ => gate.Task;

        h.Boards[SerialA].SimulateKeyPress(1);    // pressed while "main" is on screen
        h.Supervisor.SetActivePage(SerialA, "two"); // the page changes before the press is handled
        gate.SetResult();

        Assert.True(await WaitForAsync(() => Payloads("cmd/page").Length == 1));
        Assert.Equal(["main"], Payloads("cmd/page"));
    }

    [Fact]
    public async Task SlowPressOnOneDevice_DoesNotBlockAnother()
    {
        var h = await StartAsync(
            [new ButtonGridPage("main", [Key(0, "main", new PublishAction("cmd/who", "x"))])],
            brightness: null, startService: true, SerialA, SerialB);
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.BeforePressHandled = serial => serial == SerialA ? gateA.Task : Task.CompletedTask;

        h.Boards[SerialA].SimulateKeyPress(0); // stuck behind the gate
        h.Boards[SerialB].SimulateKeyPress(0);

        Assert.True(await WaitForAsync(() => Payloads("cmd/who").Length == 1), "device B waited on device A");
        gateA.SetResult();
        Assert.True(await WaitForAsync(() => Payloads("cmd/who").Length == 2));
    }

    [Fact]
    public async Task PressBeforeTheServiceStarts_IsIgnored_AndPressesWorkOnceItHas()
    {
        var h = await StartAsync([new ButtonGridPage("main", [
            Key(0, "main", new PublishAction("cmd/start", "early")),
            Key(1, "main", new PublishAction("cmd/start", "late")),
        ])], startService: false);

        h.Boards[SerialA].SimulateKeyPress(0); // the board is live, the MQTT service isn't yet
        await h.Service.StartAsync(_cts.Token);
        Assert.True(await WaitForAsync(() => h.Service.IsConnected));
        h.Boards[SerialA].SimulateKeyPress(1);

        // Had the early press been queued, it would have been handled before this one.
        Assert.True(await WaitForAsync(() => Payloads("cmd/start").Length == 1));
        Assert.Equal(["late"], Payloads("cmd/start"));
    }

    [Fact]
    public async Task PressWhileDark_IsSwallowed_AndPublishesNothing()
    {
        using var brightness = new BrightnessController(Options.Create(new BrightnessOptions()));
        var h = await StartAsync([new ButtonGridPage("main", [
            Key(0, "main", new PublishAction("cmd/dark", "first")),
            Key(1, "main", new PublishAction("cmd/dark", "second")),
        ])], brightness);
        brightness.SetDesired(0);

        h.Boards[SerialA].SimulateKeyPress(0); // wakes the deck only
        h.Boards[SerialA].SimulateKeyPress(1); // awake now: runs

        // Presses are handled in order, so once "second" arrives "first" would already have.
        Assert.True(await WaitForAsync(() => Payloads("cmd/dark").Length == 1));
        Assert.Equal(["second"], Payloads("cmd/dark"));
    }
}
