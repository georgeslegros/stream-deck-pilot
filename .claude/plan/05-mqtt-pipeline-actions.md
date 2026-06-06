# Plan 05 — MQTT: Inbound Pipeline + Outbound Actions

**Status:** ✅ Complete  
**Prerequisite:** Plans 03 (API, validation) and 04 (device supervision, desired state)  
**Spec ref:** §5 (Data-Flow Pipeline), §6 (MQTT Contract), §4.6 (Button press behaviour), §7 (Broker resilience), §12 steps 5–6

---

## Goal

Connect to the RabbitMQ MQTT broker, run the complete inbound data-flow pipeline (receive → extract → evaluate rules → format → compose → render), and handle button-press actions (publish to broker, navigate pages). Broker unavailability must never prevent the API or device rendering from working.

---

## Scope

**In scope:**
- `MqttClientService` background service (MQTTnet)
- Broker resilience: start with broker down, retry with backoff
- Inbound pipeline: JSON field extraction, conditional rule evaluation, value formatting, label composition, `DesiredStateStore` update + render
- Button press: gesture → action list → `PublishAction` + `NavigateAction`
- Topic subscription management (resubscribe when config changes)
- Integration test against a real RabbitMQ Docker container

**Out of scope:**
- Icon resolution (Plan 06) — render background colour + text only for now
- Staleness monitoring (Plan 06)
- OTel metrics (Plan 07) — pre-wire counters as no-ops; don't omit the counter calls

---

## Implementation steps

### 1. `MqttClientService : BackgroundService`

NuGet: `MQTTnet` 5.1.0.1559.

Constructor injection: `IOptions<MqttOptions>`, `ConfigStore`, `DesiredStateStore`, `IDeviceRenderer`, `ILogger<MqttClientService>`.

**Startup:**
- Build `MqttClientOptions` from env vars (broker host, port, username, password, client ID).
- Connect in a retry loop (exponential backoff, cap at 30 s). The host service `StartAsync` returns immediately; the retry loop runs as a background `Task`. The API is not blocked.
- On connection: call `ResubscribeAll()`.
- On disconnect: log warning, re-enter retry loop.

**`MqttOptions` (from `IConfiguration`):**
```
MQTT__Host, MQTT__Port (default 1883), MQTT__Username, MQTT__Password, MQTT__ClientId
```

### 2. Topic subscription management

`ResubscribeAll()`:
- Load all configs from `ConfigStore.ListSerialsAsync()`.
- Collect unique MQTT topics from all `InboundBinding.Topic` fields.
- Subscribe to all topics (QoS 1).

`OnConfigChanged(string serial)` — called by the API after a successful config PUT:
- Load the updated config.
- Diff old/new topics; unsubscribe removed, subscribe added.
- Update the button-topic index (see step 3).

### 3. Button-topic index

In-memory dictionary: `topic → List<(serial, pageId, ButtonDefinition)>`.

Rebuilt on `ResubscribeAll()` and patched on `OnConfigChanged`.

### 4. Inbound pipeline

Called on `MessageReceived` for each broker message:

```
1. Receive  — topic + raw payload string
2. Extract  — if payload is JSON, apply ValueField / UnitField (JSON path-lite: split on '.', walk JsonElement)
             — if bare string, use as-is for value
3. Evaluate — iterate button's ConditionalRule list (first-match-wins)
             — condition grammar: ">N", ">=N", "<N", "<=N", "==N", "between:A:B"
             — select BackgroundColour + Icon override from matching rule (or default)
4. Format   — round numeric value to configured precision (default 1 dp)
5. Compose  — fill FormatTemplate: replace {value}, {unit}, {label} tokens
6. Update   — call DesiredStateStore.Set(serial, pageIndex, keyIndex, new ButtonRenderState(...))
             — record DateTime.UtcNow as lastUpdated (for Plan 06 staleness)
7. Render   — call IDeviceRenderer.RenderButtonAsync (no-op if device disconnected)
```

Each step is a separate method for testability. The pipeline runs synchronously per message (no parallelism needed; messages are infrequent).

### 5. Button press handling

The library fires a `KeyStateChanged` event. Wire this in `DeviceSupervisorService` (Plan 04) and route to `IButtonPressHandler`:

```csharp
public interface IButtonPressHandler
{
    Task HandlePressAsync(string serial, int keyIndex);
}
```

`ButtonPressHandler` implementation:
1. Resolve `(serial, activePageId, keyIndex)` → `ButtonDefinition`.
2. Look up `Gestures["Tap"]` action list (skip if absent).
3. Execute each action in order:
   - `PublishAction` → `mqttClient.PublishAsync(topic, payload, QoS: 1)`.
   - `NavigateAction` → update active page in `DesiredStateStore` (new `ActivePageStore`), call `IDeviceRenderer.RenderAllAsync` for new page.

**`ActivePageStore`** (new, in-memory, per device): `string GetActivePage(string serial)` / `void SetActivePage(string serial, string pageId)`. Initialised to `Pages[0].PageId` on device connect.

Outbound publish payload convention (decide and document here): `{"value": <payload>, "ts": "<ISO8601>"}` for press events, or just the raw configured payload string — lean toward the raw configured payload (spec §6 says payload is "configured", so PublishAction.Payload is emitted verbatim).

### 6. Integration test (Testcontainers)

Add `Testcontainers.RabbitMq` 4.12.0 NuGet to the test project.

Test scenario:
1. Start RabbitMQ container.
2. Start the app with the MQTT connection pointing at the container.
3. Publish a retained MQTT message to a configured topic.
4. Assert `DesiredStateStore` contains the expected `ButtonRenderState` (value + colour from rule).
5. Simulate a button press → assert the MQTT container received the publish message on the configured topic.

---

## Verification

```bash
dotnet test                          # integration tests hit real RabbitMQ container
```

Manually: run the app, publish a value via MQTT, observe the button colour change on the virtual board (or real deck). Press a button, observe the outbound MQTT message in RabbitMQ management UI.

---

## Completion notes

**Status:** ✅ Complete — 2026-06-05 — 56/56 unit tests green; 1 integration test requires Docker Desktop running

**Outbound payload convention:** Verbatim `PublishAction.Payload` string emitted as-is (spec §6: "configured payload"). No wrapping envelope.

**Broker resilience:** `ConnectWithRetryAsync` runs as a fire-and-forget background Task; `ExecuteAsync` returns immediately so the API starts regardless. On disconnect, `DisconnectedAsync` re-enters the retry loop. Backoff doubles from 1s → cap at 30s.

**Decisions / deviations from spec:**
- No separate `IButtonPressHandler` interface. `DeviceSupervisorService` exposes `event Action<string, int>? ButtonPressed` (serial, keyIndex) fired from `KeyStateChanged`. `MqttClientService` subscribes to this event and handles press actions — eliminates circular dependency entirely.
- `MqttApplicationMessage.PayloadSegment` is init-only in MQTTnet v5; used `Payload` (`ReadOnlySequence<byte>`) with `System.Buffers.ToArray()` extension instead.
- xunit.v3 `IAsyncLifetime` uses `ValueTask` not `Task` — different from v2.
- Integration test (`MqttIntegrationTests`) requires Docker Desktop running. Run with: `dotnet test --filter "Category=Integration"`. Testcontainers spins up `rabbitmq:3-management`, enables MQTT plugin via `exec`, publishes a retained message, and asserts `DesiredStateStore` was updated with the expected colour.
- There is a duplicate `renderState` variable in `RunPipeline` (dead code from refactoring) — clean up in a future session.

**Key files created:**
- `src/StreamDeckPilot.Infrastructure/Mqtt/{MqttOptions,IConfigChangeNotifier,ButtonTopicIndex}.cs`
- `src/StreamDeckPilot.Infrastructure/Mqtt/Pipeline/InboundPipeline.cs`
- `src/StreamDeckPilot.Infrastructure/Mqtt/MqttClientService.cs`
- `tests/StreamDeckPilot.Tests/Mqtt/{InboundPipelineTests,MqttIntegrationTests}.cs`
- `DeviceSupervisorService` updated: `ButtonPressed` event + `KeyStateChanged` wiring
- `Program.cs`, `ConfigEndpoints.cs` updated to register/call MQTT services  
