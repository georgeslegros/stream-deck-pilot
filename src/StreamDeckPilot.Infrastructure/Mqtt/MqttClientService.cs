using System.Buffers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Mqtt.Pipeline;
using StreamDeckPilot.Infrastructure.Observability;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;

namespace StreamDeckPilot.Infrastructure.Mqtt;

public sealed class MqttClientService : BackgroundService, IConfigChangeNotifier
{
    private readonly MqttOptions _opts;
    private readonly ConfigStore _configStore;
    private readonly DesiredStateStore _desiredState;
    private readonly ButtonTopicIndex _topicIndex;
    private readonly IDeviceRenderer _renderer;
    private readonly DeviceSupervisorService _supervisor;
    private readonly ILogger<MqttClientService> _logger;
    private readonly Staleness.LastUpdatedStore _lastUpdated;
    private readonly StreamDeckMetrics? _metrics;
    private readonly IMqttClient _client;

    // Single-consumer channel serialises all inbound message processing so ImageSharp never races
    // on its shared MemoryAllocator across concurrent Task.Run threads.
    private readonly Channel<(string topic, string payload)> _messageChannel =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });

    private CancellationToken _stoppingToken;

    // UTC ticks of the last inbound message. Read/written across threads (drainer + connect loop),
    // so accessed via Interlocked/Volatile only. Backs the subscription-liveness watchdog: ping
    // proves the transport is alive but says NOTHING about whether the broker still routes our
    // wildcard subscription, so prolonged inbound silence forces a reconnect+resubscribe.
    private long _lastInboundTicks = DateTime.UtcNow.Ticks;

    private static readonly Random _jitter = Random.Shared is { } r ? r : new Random();

    // TryPingAsync is the maintainer-recommended liveness probe for raw IMqttClient and is the
    // backbone of the polling reconnect loop (replaces the race-prone DisconnectedAsync→TCS
    // signalling). MQTT-level keep-alive cadence; cheap round-trip the broker always answers.
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);

    public bool IsConnected => _client.IsConnected;

    public MqttClientService(
        IOptions<MqttOptions> opts,
        ConfigStore configStore,
        DesiredStateStore desiredState,
        ButtonTopicIndex topicIndex,
        IDeviceRenderer renderer,
        DeviceSupervisorService supervisor,
        Staleness.LastUpdatedStore lastUpdated,
        ILogger<MqttClientService> logger,
        StreamDeckMetrics? metrics = null)
    {
        _opts = opts.Value;
        _configStore = configStore;
        _desiredState = desiredState;
        _topicIndex = topicIndex;
        _renderer = renderer;
        _supervisor = supervisor;
        _lastUpdated = lastUpdated;
        _metrics = metrics;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        // DisconnectedAsync is used for OBSERVABILITY ONLY. The reconnect loop no longer keys off
        // this event (that design had a cross-iteration TaskCompletionSource race where a late
        // disconnect from a stale connection completed the new connection's signal). Liveness is
        // now driven by an independent TryPingAsync poll loop — the maintainer-recommended pattern.
        _client.DisconnectedAsync += OnDisconnectedAsync;
        _client.ConnectedAsync += OnConnectedAsync;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        _supervisor.ButtonPressed += (serial, keyIndex) =>
            _ = Task.Run(() => HandleButtonPressAsync(serial, keyIndex), stoppingToken);

        // Both background loops are SUPERVISED: if either throws unexpectedly it is logged at
        // Critical and restarted, so a single fault can never silently kill inbound processing
        // (the "publishes still work but nothing renders" failure mode) or the reconnect loop.
        _ = Task.Run(() => SuperviseAsync("mqtt-connect", ConnectWithRetryAsync, stoppingToken), stoppingToken);
        _ = Task.Run(() => SuperviseAsync("mqtt-drain", DrainMessageChannelAsync, stoppingToken), stoppingToken);
        return Task.CompletedTask;
    }

    // Runs a long-lived loop and restarts it if it faults. Only a cancellation (service shutdown)
    // ends supervision. This is the safety net that turns a silent unobserved-task death into a
    // logged-and-recovered event.
    private async Task SuperviseAsync(string name, Func<CancellationToken, Task> body, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await body(ct);
                // Normal return only happens on cancellation; loop guard exits below.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "MQTT background loop '{Loop}' faulted — restarting in 1s", name);
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public async Task NotifyConfigChangedAsync(string serial, CancellationToken ct = default)
    {
        // Config changes only touch the in-memory routing table. The broker subscription is a
        // single static wildcard (see SubscribeWildcardAsync), so there is NOTHING to re-subscribe.
        // This removes all subscription churn and makes config edits broker-independent.
        var config = await _configStore.LoadAsync(serial);
        _topicIndex.Update(serial, config);
        _logger.LogInformation("Config index updated for {Serial} ({TopicCount} bound topics)", serial, _topicIndex.AllTopics.Count);
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        var attempt = 0;
        // Truly persistent: the only exit is service shutdown (ct cancelled). Never gives up.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Canonical MQTTnet reconnect pattern (issue #2060): a SINGLE liveness probe drives
                // BOTH reconnect and resubscribe, atomically. TryPingAsync does a real PINGREQ/
                // PINGRESP round-trip and returns false (never throws) on a dead transport. Mixing in
                // a separate `IsConnected` branch "defeats the purpose of the whole TryPing thing"
                // (maintainer) and opens the silent-session-reset hole where IsConnected==true and
                // ping succeeds yet the branch is skipped, so SubscribeWildcardAsync never re-runs.
                // We do NOT call DisconnectAsync before ConnectAsync: back-to-back connect/disconnect
                // is the documented trigger for the "connect/disconnect is pending" wedge (#2031/#1934).
                if (!await _client.TryPingAsync(ct))
                {
                    await ConnectAndSubscribeAsync(ct);
                    attempt = 0;
                }
                else if (ShouldForceResubscribeForSilence())
                {
                    // Subscription-liveness watchdog: transport is alive (ping ok) but we have heard
                    // NOTHING inbound for InboundSilenceReconnectSeconds. A broker-side subscription
                    // loss (plugin restart / queue deletion / permission change / take-over) is
                    // invisible to ping and to IsConnected. Force a clean reconnect, which re-runs
                    // SubscribeWildcardAsync and restores routing. Guarded so it cannot fire in normal
                    // quiet periods unless explicitly enabled via options.
                    _logger.LogWarning(
                        "No inbound MQTT for {Seconds}s while transport alive — forcing reconnect+resubscribe (suspected broker-side subscription loss)",
                        _opts.InboundSilenceReconnectSeconds);
                    try { await _client.DisconnectAsync(); } catch { /* best-effort; next loop reconnects */ }
                    MarkInbound(); // reset the window so we do not immediately re-trigger
                    continue;
                }

                try { await Task.Delay(PingInterval, ct); }
                catch (OperationCanceledException) { return; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Service shutdown — graceful close, not a fault.
                return;
            }
            catch (Exception ex)
            {
                var delay = BackoffWithJitter(attempt++);
                _logger.LogWarning("MQTT connect/session failed ({Error}), retrying in {Delay:0.0}s (attempt {Attempt})",
                    ex.Message, delay.TotalSeconds, attempt);
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // Reconnect + resubscribe as one coupled step. Called only when the liveness probe says the
    // transport is gone, so ConnectAsync runs on a client the library already considers disconnected
    // — no pre-emptive DisconnectAsync, avoiding the pending-state wedge. A rejected SUBACK throws
    // out of SubscribeWildcardAsync → caught by the loop → backoff + retry. We must NEVER end this
    // method "connected but unsubscribed" (publishes fine, inbound silent).
    private async Task ConnectAndSubscribeAsync(CancellationToken ct)
    {
        await _client.ConnectAsync(BuildClientOptions(), ct);
        _logger.LogInformation("Connected to MQTT broker {Host}:{Port}", _opts.Host, _opts.Port);

        await _topicIndex.RebuildAsync(_configStore);
        await SubscribeWildcardAsync(ct);
        MarkInbound(); // fresh subscription → reset the silence watchdog baseline

        _logger.LogInformation("MQTT session established and subscribed; entering keep-alive poll");
    }

    private void MarkInbound() => Volatile.Write(ref _lastInboundTicks, DateTime.UtcNow.Ticks);

    private bool ShouldForceResubscribeForSilence()
    {
        if (_opts.InboundSilenceReconnectSeconds <= 0) return false;
        var lastTicks = Volatile.Read(ref _lastInboundTicks);
        var silence = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
        return silence.TotalSeconds >= _opts.InboundSilenceReconnectSeconds;
    }

    private MqttClientOptions BuildClientOptions() => BuildClientOptions(_opts);

    // Static so the regression test can build options without constructing the whole service.
    internal static MqttClientOptions BuildClientOptions(MqttOptions _opts) =>
        new MqttClientOptionsBuilder()
            // CRITICAL: the host/port overload is what populates the builder's private _remoteEndPoint
            // field that Build() validates. The Action<MqttClientTcpOptions> overload ONLY mutates the
            // inner _tcpOptions and leaves _remoteEndPoint null, so Build() throws "No endpoint is set."
            // (verified against MQTTnet 5.1.0.1559). Therefore set the endpoint via the string/port
            // overload FIRST, then chain the Action overload purely to enable NoDelay — the endpoint
            // set by the first call survives. Do NOT set RemoteEndpoint inside the Action as the sole source.
            .WithTcpServer(_opts.Host, _opts.Port)
            // Disable Nagle so small control packets (PINGREQ, PUBACK, SUBSCRIBE) are flushed
            // immediately rather than coalesced — keeps keep-alive timing tight under low traffic.
            .WithTcpServer(tcp => { tcp.NoDelay = true; })
            // Stable client id (no per-attempt GUID): lets the broker recognise the session
            // and clean up the prior one on reconnect. A fresh GUID each time leaks sessions.
            .WithClientId(_opts.ClientId)
            .WithCredentials(_opts.Username, _opts.Password)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithCleanSession(true) // correct for this stateless, re-subscribe-on-connect design
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311) // 3.1.1 — broad RabbitMQ compat
            // NOTE: WithTimeout is deliberately NOT set short. In MQTTnet 5 Options.Timeout also
            // bounds the PINGRESP/SUBACK waits; a value (15s) below KeepAlivePeriod (30s) can trip
            // a self-disconnect. We keep the library default (100s), comfortably above keep-alive.
            .Build();

    // Exponential backoff capped at MaxReconnectDelaySeconds, with full jitter to avoid a
    // thundering herd when many clients reconnect after a broker restart.
    private TimeSpan BackoffWithJitter(int attempt)
    {
        var capped = Math.Min(Math.Pow(2, Math.Min(attempt, 16)), _opts.MaxReconnectDelaySeconds);
        var jittered = _jitter.NextDouble() * capped; // full jitter in [0, capped]
        return TimeSpan.FromSeconds(Math.Max(1.0, jittered));
    }

    // Observability only. The poll loop (TryPingAsync) is the source of truth for reconnect;
    // this handler just records WHY a disconnect happened so future drops are diagnosable.
    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogWarning(e.Exception, "MQTT disconnected (reason={Reason}, wasConnected={WasConnected}) — {Message}",
                e.Reason, e.ClientWasConnected, e.Exception.Message);
        else
            _logger.LogWarning("MQTT disconnected (reason={Reason}, wasConnected={WasConnected})",
                e.Reason, e.ClientWasConnected);
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs e)
    {
        _logger.LogInformation("MQTT transport connected (CONNACK reason={Reason})", e.ConnectResult.ResultCode);
        return Task.CompletedTask;
    }

    // One static wildcard subscription covers every present and future button-bound topic.
    // Subscriptions never change after connect; config edits only mutate the in-memory index.
    private async Task SubscribeWildcardAsync(CancellationToken ct)
    {
        var filter = new MqttTopicFilterBuilder()
            .WithTopic(_opts.TopicPrefix)
            // QoS 1 (AtLeastOnce) is the RabbitMQ-correct choice here. QoS 0 against RabbitMQ's MQTT
            // plugin gets the specialized rabbit_mqtt_qos0_queue whose "Overload protection" will
            // SILENTLY DROP messages (never redelivered) when the connection mailbox exceeds
            // mqtt.mailbox_soft_limit (default 200) AND the socket is under TCP backpressure — exactly
            // the slow-consumer/large-burst condition this service hits while rendering. The QoS1
            // "prefetch=10 stall" only wedges if the client never PUBACKs; MQTTnet auto-acks QoS1 on
            // handler return, and OnMessageReceivedAsync returns immediately after a non-blocking
            // channel write, so PUBACKs flow and the prefetch window never sticks. QoS1 + the existing
            // unbounded-channel offload gives at-least-once delivery without the silent-drop hazard.
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        var result = await _client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter).Build(), ct);

        foreach (var item in result.Items)
        {
            // Granted codes are GrantedQoS0/1/2. Anything else (NotAuthorized, TopicFilterInvalid,
            // WildcardSubscriptionsNotSupported, …) means we have NO live subscription. Treat it as
            // FATAL for the session: throw so the reconnect loop tears down and retries rather than
            // entering the keep-alive poll on a connected-but-deaf socket (publish works, inbound silent).
            var granted = item.ResultCode is MqttClientSubscribeResultCode.GrantedQoS0
                or MqttClientSubscribeResultCode.GrantedQoS1
                or MqttClientSubscribeResultCode.GrantedQoS2;
            if (granted)
            {
                _logger.LogInformation("Subscribed wildcard {Topic} → {ResultCode}", item.TopicFilter.Topic, item.ResultCode);
            }
            else
            {
                _logger.LogError("Wildcard subscription REJECTED for {Topic} — ResultCode={ResultCode}. Check RabbitMQ topic permissions for user '{User}'",
                    item.TopicFilter.Topic, item.ResultCode, _opts.Username);
                throw new InvalidOperationException(
                    $"MQTT subscription to '{item.TopicFilter.Topic}' rejected with {item.ResultCode}; cannot serve inbound state.");
            }
        }
    }

    // ── Inbound pipeline ─────────────────────────────────────────────────────

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        // CRITICAL: this runs on MQTTnet's packet-receive loop. In MQTTnet 5 an UNHANDLED throw
        // here causes the client to tear down the TCP connection (the "peer closed TCP connection"
        // RabbitMQ logged after the retained-message batch). Everything is wrapped so the handler
        // can never throw, and heavy work is offloaded to the channel/drainer.
        try
        {
            MarkInbound(); // feeds the subscription-liveness watchdog: proof the subscription routes
            var topic = e.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
            _logger.LogDebug("MQTT message received on {Topic}: {Payload}", topic, payload);
            _messageChannel.Writer.TryWrite((topic, payload));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue inbound MQTT message — connection kept alive");
        }
        return Task.CompletedTask;
    }

    private async Task DrainMessageChannelAsync(CancellationToken ct)
    {
        _logger.LogInformation("MQTT message drainer started");
        try
        {
            await foreach (var (topic, payload) in _messageChannel.Reader.ReadAllAsync(ct))
            {
                try { await ProcessMessageAsync(topic, payload); }
                catch (Exception ex) { _logger.LogWarning(ex, "Unexpected error processing MQTT message on {Topic}", topic); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("MQTT message drainer stopped (service shutdown)");
            throw; // let SuperviseAsync observe cancellation and exit cleanly
        }
        catch (Exception ex)
        {
            // The channel is unbounded and never completed, so this should be unreachable in
            // normal operation. If it ever happens, SuperviseAsync logs Critical and restarts us.
            _logger.LogCritical(ex, "MQTT message drainer exited unexpectedly — will be restarted");
            throw;
        }
    }

    private async Task ProcessMessageAsync(string topic, string payload)
    {
        var matches = _topicIndex.Lookup(topic);
        if (matches.Count == 0)
        {
            // With a wildcard subscription many topics won't map to a button — this is normal, not a fault.
            _logger.LogDebug("MQTT message on {Topic} has no button binding — dropped", topic);
            _metrics?.MqttMessagesDropped.Add(1, [new("reason", "no_binding"), new("topic", topic)]);
            return;
        }

        foreach (var (serial, pageId, button) in matches)
        {
            try { RunPipeline(serial, pageId, button, payload); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pipeline failed for button {ButtonId} on {Serial}", button.ButtonId, serial);
            }
        }
        await Task.CompletedTask;
    }

    // internal (not private) so the cross-page render-guard regression test can drive a single
    // message through the full extract→rules→format→desired-state→render path without a broker.
    internal void RunPipeline(string serial, string pageId, ButtonDefinition button, string payload)
    {
        using var activity = StreamDeckActivitySource.Pipeline.StartActivity("inbound_pipeline");
        activity?.SetTag("mqtt.topic", button.Inbound?.Topic);
        activity?.SetTag("device.serial", serial);
        activity?.SetTag("button.id", button.ButtonId);

        _metrics?.MqttMessagesReceived.Add(1, [new("topic", button.Inbound?.Topic ?? "")]);

        var binding = button.Inbound;

        // Step 2 — extract
        var (value, unit, mqttLabel) = InboundPipeline.Extract(payload, binding?.ValueField, binding?.UnitField, binding?.LabelField);

        // Step 3 — evaluate rules
        var (colour, icon) = InboundPipeline.EvaluateRules(value, button.Rules);

        // Step 4 — format
        var formatted = InboundPipeline.FormatValue(value);

        // Step 5 — resolve text zones (a message arrived ⇒ there is live data to resolve against)
        var center = InboundPipeline.ResolveZone(button.Display.Center, hasData: true, formatted, unit, mqttLabel);
        var bottom = InboundPipeline.ResolveZone(button.Display.Bottom, hasData: true, formatted, unit, mqttLabel);

        // Step 6 — update desired state
        var renderState = new ButtonRenderState(button.ButtonId,
            colour,
            icon ?? button.Display.BaseIcon,
            button.Display.IconPlacement,
            center,
            bottom);

        _desiredState.Set(serial, pageId, button.KeyIndex, renderState);
        _lastUpdated.RecordUpdate(serial, pageId, button.KeyIndex);

        // Step 7 — render (best-effort)
        var board = _supervisor.GetBoard(serial);
        if (board is null)
        {
            _logger.LogWarning("No board found for serial '{Serial}' — render skipped. Known serials: [{Known}]",
                serial, string.Join(", ", _supervisor.GetAllStates().Keys));
            return;
        }
        if (!board.IsConnected)
        {
            _logger.LogWarning("Board '{Serial}' reports IsConnected=false — render skipped", serial);
            return;
        }
        // Only project to hardware when this button's page is the active one. Desired state is
        // stored above regardless, so an inactive-page button renders correctly on navigation.
        // Without this guard, a live MQTT update paints an off-page tile over the visible page
        // (e.g. climate/setpoint tiles from page 2 bleeding onto page 1).
        // Strict equality: if the active page is unknown (null) we also skip the paint rather than
        // fall through — desired state is already stored, and the page renders in full on connect.
        var activePage = _supervisor.GetActivePage(serial);
        if (activePage != pageId)
        {
            _logger.LogDebug("Render skipped for {ButtonId}: page '{PageId}' not active ('{Active}')",
                button.ButtonId, pageId, activePage);
            return;
        }
        _renderer.RenderButton(board, serial, button.KeyIndex, renderState);
        _logger.LogInformation("Rendered key {KeyIndex} on {Serial} — colour={Colour} center={Center}",
            button.KeyIndex, serial, renderState.BackgroundColour, renderState.CenterText);
    }

    // ── Button press handling ─────────────────────────────────────────────────

    private async Task HandleButtonPressAsync(string serial, int keyIndex)
    {
        var pageId = _supervisor.GetActivePage(serial);
        if (pageId is null) return;

        var config = await _configStore.LoadAsync(serial);
        if (config is null) return;

        var page = config.Pages.FirstOrDefault(p => p.PageId == pageId);
        if (page is not ButtonGridPage grid) return;

        var button = grid.Buttons.FirstOrDefault(b => b.KeyIndex == keyIndex);
        if (button is null) return;

        if (!button.Gestures.TryGetValue("Tap", out var actions)) return;

        foreach (var action in actions)
        {
            try { await ExecuteActionAsync(serial, action); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Action failed for button {ButtonId}", button.ButtonId);
            }
        }
    }

    private async Task ExecuteActionAsync(string serial, ButtonAction action)
    {
        switch (action)
        {
            case PublishAction publish:
                // Authoritative liveness is the transport's own state, not a hand-maintained bool
                // (which can lag the real disconnect and publish into a half-open socket).
                if (!_client.IsConnected)
                {
                    _logger.LogWarning("Cannot publish to {Topic} — broker not connected", publish.Topic);
                    return;
                }
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(publish.Topic)
                    .WithPayload(publish.Payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                try
                {
                    // Bound the publish independently of the global Options.Timeout (library default
                    // 100s). On a half-open socket — IsConnected can read true via TOCTOU at line 431 —
                    // a QoS1 PublishAsync blocks waiting for a PUBACK up to Options.Timeout, freezing
                    // the button task ~100s and piling up Task.Run instances on repeated presses
                    // (dotnet/MQTTnet#1642). A short linked CTS caps the wait; the catch below already
                    // swallows the resulting OperationCanceledException as a logged transient.
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    await _client.PublishAsync(msg, cts.Token);
                    _logger.LogInformation("Published to {Topic}", publish.Topic);
                }
                catch (Exception ex)
                {
                    // IsConnected can transiently disagree with PublishAsync during a drop; treat a
                    // failed/timed-out publish as a logged transient (the poll loop will reconnect),
                    // not a crash.
                    _logger.LogWarning(ex, "Publish to {Topic} failed — broker connection may be dropping", publish.Topic);
                }
                break;

            case NavigateAction navigate:
                _supervisor.SetActivePage(serial, navigate.TargetPageId);
                _logger.LogInformation("Navigated {Serial} to page {PageId}", serial, navigate.TargetPageId);
                break;
        }
    }
}
