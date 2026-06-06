# Investigation: MQTT no-rerender + serial control-char prefix

## Issue 1 — MQTT state changes don't re-render

### Root cause (primary): reconnect storm caused by a self-inflicted disconnect loop
`ConnectWithRetryAsync` (MqttClientService.cs:88) uses `.WithClientId(_opts.ClientId)`
with a **fixed client id** and `.WithCleanSession(true)`. The session memory records the
exact symptom: *"MQTT reconnects every ~1 second, indefinitely. No Message received."*

Mechanism: if any second client (a debug client, a second container instance, an HA test,
or a leftover session) connects with the **same client id**, MQTT brokers (RabbitMQ MQTT
plugin included) *force-disconnect the older session*. That fires `OnDisconnectedAsync`
(line 131) → `_disconnectedSignal.TrySetResult()` → the loop reconnects → steals the
session back → the other client gets kicked → ping-pong every ~1s. During this storm the
client is almost never in the subscribed-and-connected window long enough to deliver
non-retained live messages, so only the retained message that arrives immediately after
SUBSCRIBE (sometimes) gets through. **Fix: unique client id**, e.g.
`_opts.ClientId + "-" + Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8]`.

### Root cause (secondary / independent): subscriptions are NOT renewed after a config write while connected — and the retained-only symptom
- `NotifyConfigChangedAsync` (line 75) DOES call `ResubscribeAllAsync` when `_isConnected`.
  That part is correct. BUT it only *adds* topic filters; it never *unsubscribes* removed
  topics (minor, not the reported bug).
- The reported "only initial retained message processed" is fully consistent with the
  reconnect storm above: a fresh `CleanSession=true` SUBSCRIBE re-delivers the retained
  message each reconnect, giving the illusion that "only retained works."

### Ruled OUT as causes
- **No throttle/dedup/same-value suppression** anywhere. `RunPipeline` always renders.
- **StalenessMonitor** only dims/un-dims; it never blocks the MQTT pipeline render.
- **`board.IsConnected`** — guarded (line ~ render step) but would log
  "IsConnected=false — render skipped"; not the reported symptom.
- **`GetBoard(serial)` null** — would log "No board found ... Known serials: [...]".
  This IS a real risk via Issue 2 (serial mismatch) — see below.
- **Topic index race**: `RebuildAsync` runs BEFORE `ResubscribeAllAsync` on every connect
  (lines 112-113), and the index is a `volatile` swapped dictionary — no race.

### Diagnostic logging to add (definitively distinguishes retained vs live)
In `OnMessageReceivedAsync` (line 171), log the retain flag + dup flag:
```csharp
_logger.LogInformation(
    "MQTT msg on {Topic} retain={Retain} dup={Dup} qos={Qos}: {Payload}",
    topic, e.ApplicationMessage.Retain, e.ApplicationMessage.Dup,
    e.ApplicationMessage.QualityOfServiceLevel, payload);
```
If live ON→OFF changes never appear here while retained ones do → confirms the
subscribe/connection-stability problem (Issue 1 primary). Also add a counter log in
`OnDisconnectedAsync` — if you see a disconnect every ~1s, the reconnect storm is confirmed.
Add at top of `ConnectWithRetryAsync` loop: `_logger.LogInformation("MQTT connect attempt, clientId={Id}", _opts.ClientId);`

### Correct fix if topics aren't subscribed after a config write
`NotifyConfigChangedAsync` already re-subscribes. Harden it:
1. Unique client id (above) — eliminates the storm.
2. In `ResubscribeAllAsync`, also `UnsubscribeAsync` topics no longer in the index.
3. Optionally set `.WithCleanSession(false)` + persistent unique id so the broker keeps the
   subscription across brief reconnects (but unique id is the real fix).

## Issue 2 — serial has a `` (Shift Out, ASCII 14) prefix

### What's happening
- `board.GetSerialNumber()` (DeviceSupervisorService.cs, ScanAsync) returns the raw HID
  string. For Stream Deck MK.2 the HID feature report that carries the serial has a
  **report-id / length prefix byte that StreamDeckSharp does not strip** for the
  Scissor-Switch PID (the same PID that was missing and required manual registration —
  see MEMORY.md). That leading byte decodes to ``. So the device-side serial is
  `"A7FZA5191LB60S"`.
- **There is NO sanitization anywhere in `src`** (grep for TrimStart / control-char /
  Normalize / Sanitize returns nothing).
- The serial flows UN-trimmed into:
  - `_boards[serial]` and `_states[serial]` (DeviceSupervisorService)
  - `catalogue` via `AppendDeviceAsync(serial, ...)`
  - the config file, because the UI/user copies the serial *from the catalogue*, so the
    submitted config serial ALSO carries `` (matches the observed
    `"A7FZA5191LB60S"` in the API payload).

### Is it a real mismatch risk? Yes — latent.
Today everything matches *because every path uses the same prefixed string* (that's why
the log showed `Rendered key 0 on A7FZA5191LB60S` — both sides prefixed). The danger:
- `ButtonTopicIndex` uses `StringComparer.Ordinal`; `_boards`/`_states` are ordinal too.
  Any path where the prefix is stripped on ONE side (e.g. a human types the clean serial
  into the config, or a future trim is added in one place) → `GetBoard(serial)` returns
  null → "No board found" → silent render skip. This is fragile and should be fixed.

### Recommended fix (normalize at the single source)
Sanitize the serial the moment it leaves the device, in `ScanAsync`:
```csharp
string serial;
try { serial = SanitizeSerial(board.GetSerialNumber()); }
...
// helper:
private static string SanitizeSerial(string raw) =>
    new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
```
Because the catalogue is then written with the clean serial, configs submitted afterward
also use the clean serial, and all dictionaries stay consistent. Add a one-time migration
note: existing catalogue/config files on the volume still hold the prefixed serial and
must be re-saved (or the device re-discovered) after the fix.
