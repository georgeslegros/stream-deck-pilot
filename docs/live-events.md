# Live events

Everything that changes at runtime is **pushed** to clients as it happens; nothing polls (#108).
One in-process event bus carries the events. The web UI listens to it directly; external clients
(scripts, dashboards, Home Assistant) use one of two endpoints:

| Endpoint | Protocol | Good for |
|---|---|---|
| `GET /api/events` | Server-Sent Events (`text/event-stream`) | `curl`, scripts, anything that reads an HTTP stream |
| `/hubs/live` | SignalR (WebSockets, falling back to SSE / long polling) | .NET / JavaScript clients that want SignalR's reconnect and groups |

Both need the API key. Both start with a **snapshot** of the current state, then send **events**,
each a delta to apply on top.

## Events

Every event is a JSON object with these common fields:

| Field | Meaning |
|---|---|
| `type` | The event name (also the SSE `event:` field). |
| `sequence` | Position in this run's event sequence. Increases by one per delivered event. |
| `at` | When it was delivered (ISO 8601). |
| `serial` | The deck it concerns. Absent for service-wide events. |

| `type` | Extra fields | Per deck | When |
|---|---|---|---|
| `DeviceConnected` | — | yes | A deck connected (or reconnected). |
| `DeviceDisconnected` | — | yes | A deck was unplugged or released. |
| `KeyRendered` | `key` (0-based), `hash` | yes | A key now shows a different image. Fetch `/devices/{serial}/keys/{key}.png`; `hash` is the image's content hash (`black` for an empty key). |
| `ValueReceived` | `topic`, `value` | no | A message arrived on a topic that a button or control is bound to (after the keys took it). `value` is the raw payload, cut to `Events__MaxValueLength` characters. |
| `BrokerOnline` | — | no | The MQTT session is up (connected and subscribed). |
| `BrokerOffline` | `offlineIndicator` | no | The session dropped (`false`), and again once the deck shows the offline look (`true`). |
| `ConfigChanged` | — | yes | A config upload was saved and applied. |
| `ConfigRejected` | `errors` | yes | A config upload was refused; nothing changed. |
| `PageChanged` | `pageId` | yes | The deck shows another page (navigation, config change, inactivity). |
| `BrightnessChanged` | `level` (0–100) | no | The brightness the decks run at changed. |

Example:

```json
{"type":"KeyRendered","key":3,"hash":"9f2c41d07a6be583","sequence":1842,"at":"2026-10-23T18:04:11.204+00:00","serial":"EXAMPLESERIAL1"}
```

**Coalescing.** `KeyRendered` (per deck and key) and `ValueReceived` (per topic) are delivered at
most `Events__MaxPerSecondPerKey` times a second each (default 4). When they come faster, the
latest one wins: you may skip intermediate images or values, never the last one.

New event types may be added later (for example for profiles and sign-in). Ignore types you don't
know.

## Snapshot

```json
{
  "bootId": "3f9c0a6e21d4",
  "sequence": 1840,
  "broker": { "connected": true, "offlineIndicator": false, "lastMessageAt": "2026-10-23T18:04:10.9+00:00" },
  "brightness": 80,
  "devices": [
    {
      "serial": "EXAMPLESERIAL1", "model": "Stream Deck MK.2", "keyRows": 3, "keyColumns": 5,
      "state": "connected", "activePage": "main",
      "keys": { "0": "9f2c41d07a6be583", "1": "black" }
    }
  ],
  "values": { "sensors/office/co2": { "value": "{\"co2\":812}", "at": "2026-10-23T18:04:10.9+00:00" } }
}
```

- `state` is `unknown`, `disconnected`, `connecting`, `connected` or `faulted`.
- `keys` holds the content hash of every key painted since the deck connected.
- `values` holds the latest payload per bound topic since the service started.
- **Snapshot and events overlap.** The server subscribes you before it takes the snapshot, so an
  event can arrive that the snapshot already reflects. Applying an event is idempotent (it sets a
  state, it never adds to one), so applying it again is harmless. Still, the simple rule is: ignore
  every event whose `sequence` is at or below the snapshot's `sequence`.
- An event for a deck that isn't in your snapshot (a deck discovered just now) means: fetch a new
  snapshot.

## `GET /api/events` (Server-Sent Events)

```bash
curl -N -H "X-Api-Key: $API_KEY" "$API/api/events?types=ValueReceived,KeyRendered&serial=EXAMPLESERIAL1"
```

| Query | Meaning |
|---|---|
| `types` | Comma-separated event names (case-insensitive). Default: all. An unknown name is a `400`. |
| `serial` | Only this deck's per-deck events, and only this deck in the snapshot. Service-wide events still come through. |

The stream:

```
retry: 3000

id: 3f9c0a6e21d4:1840
event: snapshot
data: {"bootId":"3f9c0a6e21d4","sequence":1840,…}

id: 3f9c0a6e21d4:1841
event: ValueReceived
data: {"type":"ValueReceived","topic":"sensors/office/co2","value":"812",…}

: keep-alive
```

- **Keep-alive.** An idle stream gets a `: keep-alive` comment every `Events__HeartbeatSeconds`
  (default 15), so proxies don't close it.
- **Resume.** Each event's `id` is `{bootId}:{sequence}`. Reconnect with the last one in the
  `Last-Event-ID` header (browsers' `EventSource` does this for you) and you get the events you
  missed, without a snapshot, as long as they're still buffered (`Events__ReplayCapacity`, default
  1024 events). Otherwise (too long away, or the service restarted, which changes `bootId`) the
  stream starts over with a fresh `snapshot`.
- **Falling behind.** A client that reads too slowly to keep up with `Events__SubscriberCapacity`
  buffered events gets a fresh `snapshot` in the middle of the stream. Replace your state with it.
- **Proxies.** The response is unbuffered and carries `X-Accel-Buffering: no` (nginx). See
  [reverse-proxy.md](reverse-proxy.md).
- A browser's `EventSource` can't send headers, so it can't send the API key. Use the web UI, the
  hub, or a server-side client.

## `/hubs/live` (SignalR)

Authenticate with the API key in a header, either of:

- `X-Api-Key` (set it in the client's `headers` / `Headers` option),
- a Bearer token: set the client's `accessTokenFactory` / `AccessTokenProvider` to return the key
  (accepted on `/hubs/live` only).

The key is **never** accepted in the URL: SignalR's `access_token` query parameter gets a `401`.
URLs end up in host and proxy access logs, and the key would end up there with them. A consequence:
a browser can't put headers on a WebSocket or an `EventSource`, so a browser client only works over
long polling. Use a server-side or native client (the .NET client sends the Bearer header on every
transport), or the web UI, which needs none of this: it runs in-process.

Hub methods:

| Method | Returns | |
|---|---|---|
| `Subscribe(types: string[] \| null, serial: string \| null)` | snapshot | Replaces this connection's subscription; `null`/empty `types` = all. |
| `GetSnapshot(serial: string \| null)` | snapshot | The current state. |

Messages the server sends:

| Message | Argument | |
|---|---|---|
| `event` | an event | As above. |
| `resync` | — | The server fell behind and dropped events: call `GetSnapshot` and continue. |

After a reconnect (SignalR's automatic reconnect gives you a new connection), call `Subscribe`
again: it returns a fresh snapshot.

```js
const connection = new signalR.HubConnectionBuilder()
  // Outside a browser the key travels as a Bearer header. In a browser, force long polling:
  // it can't put headers on a WebSocket or an EventSource, and the key is never accepted in the URL.
  .withUrl("https://streamdeck.example.com/hubs/live", { accessTokenFactory: () => apiKey })
  .withAutomaticReconnect()
  .build();
let state;
connection.on("event", e => { if (e.sequence > state.sequence) apply(state, e); });   // skip what the snapshot covers
connection.on("resync", async () => state = await connection.invoke("GetSnapshot", null));
connection.onreconnected(async () => state = await connection.invoke("Subscribe", ["KeyRendered"], null));
await connection.start();
state = await connection.invoke("Subscribe", ["KeyRendered"], null);
```

## In the web UI

Each browser tab (Blazor circuit) gets its own subscription (`LiveState`, scoped to the circuit,
disposed with it). Components read `LiveState.Snapshot` and re-render on `Changed`. No timer drives
any UI refresh.

- While the circuit's connection is down, a banner says **Reconnecting…**. A circuit that comes back
  kept its subscription, so it is still current.
- If the server restarted, the circuit is gone; Blazor reloads the page, which starts from a fresh
  snapshot.

For a new page, inject `LiveState` (or `IDeviceDirectory` for the deck list), call `StartAsync()`
in `OnInitializedAsync`, and handle `Changed` with `InvokeAsync(StateHasChanged)`. `EventApplied`
gives you each event too, e.g. to reload one key image on `KeyRendered`. Unsubscribe in `Dispose`.

## Settings

| Variable | Meaning | Default |
|---|---|---|
| `Events__MaxPerSecondPerKey` | Coalescing: deliveries per second per key (`KeyRendered`) or topic (`ValueReceived`). | `4` |
| `Events__SubscriberCapacity` | Events buffered per subscriber before it must resync from a snapshot. | `1024` |
| `Events__ReplayCapacity` | Recent events kept for SSE resume. | `1024` |
| `Events__HeartbeatSeconds` | SSE keep-alive interval. | `15` |
| `Events__MaxValueLength` | `ValueReceived.value` is cut to this many characters. | `1024` |
