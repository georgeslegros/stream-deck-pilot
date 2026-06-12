# Stream Deck Pilot

A headless .NET service that drives Elgato Stream Deck devices over USB from inside a
Docker container. It renders button tiles that react to MQTT events (Home Assistant or any
MQTT 3.1.1 broker) and is configured entirely over a REST API. There is no UI and no database:
configuration is JSON files on a mounted volume, and the service is controlled by HTTP calls and
driven by MQTT messages. It is intended to run unattended on a Linux mini-server behind a reverse
proxy that terminates HTTPS.

This README is the operational glue layer. For deeper detail see:

- `streamdeck-service-spec.md` — authoritative architecture and design brief
- `CLAUDE.md` — architecture summary
- `docs/api-guide.md` — full REST API reference (request/response shapes, worked examples)
- `docs/icon-vocabulary.md` — icon library and `builtin:<name>` naming
- `docs/design/key-rendering-redesign.md` and `docs/readability-design-report.md` — rendering and typography

---

## 1. How it works

The service runs three independent loops inside one process.

```
   USB device                MQTT broker                  REST clients
       |                          |                            |
  [Device supervisor]      [MQTT client + pipeline]      [ASP.NET minimal API]
   enumerate / poll          subscribe wildcard            read/write config
   connect / reconnect       extract -> rule -> render     manage catalogue
       |                          |                            |
       +--------------+-----------+-------------+--------------+
                      |   in-memory DESIRED STATE (per button) |
                      +-----------------------------------------+
                                   |
                        best-effort projection to hardware
```

- **Device supervision** enumerates and connects Stream Deck hardware, runs a per-device state
  machine (`Disconnected -> Connecting -> Connected -> Faulted -> reconnecting`), and supervises
  multiple devices independently.
- **MQTT pipeline** holds a single wildcard subscription. For each inbound message it runs a fixed
  pipeline: `receive -> extract field -> evaluate rules -> format value -> compose label -> render`.
- **REST config surface** is the only way to read/write configuration. Config writes are validated
  against the device catalogue before being persisted.

The service maintains the intended ("desired") state of every button in memory at all times.
Rendering to physical hardware is a best-effort projection of that state. A device disconnect is a
non-event; on reconnect the service re-renders every button from desired state. The broker being
unreachable is a degraded state, not a crash — the API stays up regardless.

---

## 2. Prerequisites

- **Docker** on a host that supports USB/HID device passthrough — a Linux host, or Docker on Windows
  via WSL2 with the HID device attached to the WSL distro.
- **Elgato Stream Deck** — any model supported by StreamDeckSharp (Original, MK.2, XL, Mini, and
  variants). The service reads key geometry from the device at discovery time (`CountX × CountY`), so
  different grid sizes work without config changes. PID `0x00A5` (MK.2 Scissor Switch revision,
  missing from StreamDeckSharp 6.1.0) is registered at startup automatically.
- **MQTT broker** speaking MQTT 3.1.1. Tested against RabbitMQ with the `rabbitmq_mqtt` plugin; any
  compliant broker works.
- The container already installs `libhidapi-libusb0`, `libusb-1.0-0`, and `fonts-dejavu-core`
  (see the Dockerfile) — nothing extra to install on the host beyond Docker itself.

To find the right HID node on the host, attach the device and look for `/dev/hidrawN` (the supervisor
opens the Stream Deck through hidraw).

---

## 3. Quick start — Docker

Build the image from the repo root (the Dockerfile lives under the API project):

```bash
docker build -f src/StreamDeckPilot.Api/Dockerfile -t stream-deck-pilot .
```

Run it, passing the HID device, the API key, MQTT credentials, and a persistence volume:

```bash
docker run -d --name stream-deck-pilot \
  --device /dev/hidraw0 \
  -e ApiKey="$(openssl rand -hex 32)" \
  -e Storage__BaseDirectory=/data \
  -e Mqtt__Host=rabbitmq.local \
  -e Mqtt__Port=1883 \
  -e Mqtt__Username=streamdeck \
  -e Mqtt__Password=secret \
  -v stream_deck_data:/data \
  -p 8080:8080 \
  stream-deck-pilot
```

If a single `--device /dev/hidrawN` does not work (some hosts re-enumerate the node), fall back to
`--privileged -v /dev:/dev` and narrow it down once you know the stable node. Prefer the explicit
`--device` form for production.

### docker-compose

A `docker-compose.yml` is provided. It reads `API_KEY`, `MQTT_HOST`, `MQTT_PORT`, `MQTT_USERNAME`,
and `MQTT_PASSWORD` from the environment (or an `.env` file), mounts a named `stream_deck_data`
volume at `/data`, and passes through `/dev/hidraw0`:

```bash
API_KEY="$(openssl rand -hex 32)" MQTT_HOST=rabbitmq.local docker compose up -d
```

`API_KEY` is required — compose fails fast if it is unset. An observability stack lives in a separate
`docker-compose.observability.yml` (Alloy + Prometheus + Grafana).

Check health (this endpoint is exempt from auth):

```bash
curl http://localhost:8080/health
# {"status":"healthy"}
```

---

## 4. Configuration reference

### Environment variables

ASP.NET configuration keys use `__` (double underscore) to express nesting. The values below are
read by `Program.cs` and `MqttOptions.cs`.

| Variable | Purpose | Default / Required |
|---|---|---|
| `ApiKey` | Static API key checked (constant-time) on every request via the `X-Api-Key` header. If unset, every request is rejected with 401 and a warning is logged. | **Required** |
| `Storage__BaseDirectory` | Base directory for all persisted files (catalogue, configs, images). | `/data` in container |
| `Mqtt__Host` | MQTT broker hostname. | `localhost` |
| `Mqtt__Port` | MQTT broker port. | `1883` |
| `Mqtt__Username` | MQTT username. | none (anonymous) |
| `Mqtt__Password` | MQTT password. | none |
| `Mqtt__ClientId` | MQTT client identifier. | `stream-deck-pilot` |
| `Mqtt__TopicPrefix` | Single wildcard topic the service subscribes to. Per-button topics are matched in-process against this subscription. | `home/#` |
| `Mqtt__MaxReconnectDelaySeconds` | Upper bound for reconnect backoff. The loop never gives up. | `30` |
| `Mqtt__InboundSilenceReconnectSeconds` | Subscription-liveness watchdog: if no inbound message arrives within this window, force a reconnect + resubscribe. `0` disables it. | `0` (disabled) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP gRPC endpoint for metrics and traces (standard OpenTelemetry variable). | `http://localhost:4317` |

All secrets (API key, MQTT credentials, OTLP endpoint) come from environment variables / Docker
secrets only. They are never written to persisted config files or baked into the image.

### Persistent volume layout

Everything the service persists lives under `Storage__BaseDirectory` (`/data` in the container):

```
/data/
  catalog.json               # device catalogue — auto-populated on physical discovery; NEVER edit by hand
  config/
    <serial>.json            # one config file per device serial — THIS is what you edit (via the API)
  images/
    <serial>/
      <filename>             # uploaded custom icons, referenced as custom:<filename>
```

- `catalog.json` is the authority for which serials may be configured. It is populated only by
  physical discovery — never via the API. You cannot write config for a serial that is not in the
  catalogue (the device must have been plugged in at least once).
- `config/<serial>.json` is managed through the API. A connected device with no config renders blank.
- Catalogue and config are separate stores, so a bad config write can never corrupt the catalogue.
- Writes are atomic (temp file then rename), and every persisted file carries a `schemaVersion`.

---

## 5. Button config — worked example

The live homelab dashboard, rendered by the actual tile composer (`tools/StreamDeckPreview`):

![Stream Deck board — main page](docs/images/board-main.png)

Configuration is normally written with `PUT /devices/{serial}/config` (see the API section), but the
file on disk has the same shape. Below is a complete, valid `config/<serial>.json` with three tiles
on one `ButtonGrid` page: a temperature sensor, a light toggle, and a navigation button.

> JSON does not allow comments. The file below is valid JSON; field meanings are explained in the list
> that follows.

```json
{
  "schemaVersion": 2,
  "serial": "A7FZA5191LB60S",
  "pages": [
    {
      "pageType": "ButtonGrid",
      "pageId": "main",
      "buttons": [
        {
          "buttonId": "living-temp",
          "keyIndex": 0,
          "pageId": "main",
          "display": {
            "baseIcon": "builtin:thermometer",
            "iconPlacement": "corner",
            "center": { "label": null, "template": "{value}{unit}" },
            "bottom": { "label": "Living", "template": null }
          },
          "inbound": {
            "topic": "home/living/temperature",
            "valueField": "value",
            "unitField": "unit",
            "labelField": null,
            "expectsRetained": true,
            "stalenessTimeout": "00:05:00"
          },
          "rules": [
            { "condition": ">=24", "backgroundColour": "#CC3300", "icon": null },
            { "condition": "<24",  "backgroundColour": "#0066AA", "icon": null }
          ],
          "gestures": {}
        },
        {
          "buttonId": "desk-light",
          "keyIndex": 1,
          "pageId": "main",
          "display": {
            "baseIcon": "builtin:lightbulb-outline",
            "iconPlacement": "center",
            "center": null,
            "bottom": { "label": "Desk", "template": null }
          },
          "inbound": {
            "topic": "home/office/desk-light/state",
            "valueField": "on",
            "unitField": null,
            "labelField": null,
            "expectsRetained": true,
            "stalenessTimeout": null
          },
          "rules": [
            { "condition": ">=1", "backgroundColour": "#33AA33", "icon": "builtin:lightbulb" },
            { "condition": "<1",  "backgroundColour": "#222222", "icon": "builtin:lightbulb-outline" }
          ],
          "gestures": {
            "Tap": [
              { "type": "Publish", "topic": "home/office/desk-light/set", "payload": "toggle" }
            ]
          }
        },
        {
          "buttonId": "go-page2",
          "keyIndex": 14,
          "pageId": "main",
          "display": {
            "baseIcon": "builtin:chevron-right",
            "iconPlacement": "center",
            "center": null,
            "bottom": null
          },
          "inbound": null,
          "rules": [],
          "gestures": {
            "Tap": [
              { "type": "Navigate", "targetPageId": "page2" }
            ]
          }
        }
      ]
    }
  ]
}
```

Field reference (from the domain model in `src/StreamDeckPilot.Core/Models/Config/`):

- **`schemaVersion`** — schema version of this document; used by the migration runner.
- **`serial`** — must match a serial already present in `catalog.json`.
- **`pages[].pageType`** — type discriminator. `ButtonGrid` is the only type at launch.
- **`pages[].pageId`** — stable page identifier; targeted by `Navigate` actions.
- **`buttonId`** — stable, human-readable button identifier.
- **`keyIndex`** — 0-based key position, left-to-right, top-to-bottom. Range depends on the device grid (e.g. 0–14 for a 5×3, 0–31 for a 4×8 XL).
- **`display.baseIcon`** — `builtin:<mdi-name>`, `custom:<filename>`, or `null`.
- **`display.iconPlacement`** — `"corner"` (small icon, top-left) or `"center"` (large centred icon, the hero). Centre text takes precedence over a centre-placed icon. Layout is what you declare here and in the zones — it is **not** inferred from the kind of data the tile carries.
- **`display.center`** / **`display.bottom`** — text zones, each `{ "label": ..., "template": ... }` (or `null`). `center` renders large (the hero; its value/unit split on the first space), `bottom` renders as a small caption. `template` is resolved against live data (tokens `{value}`, `{unit}`, `{label}`); `label` is static and is the fallback shown when there's no live data yet. Either field may be `null`.
- **`inbound`** — MQTT binding, or `null` for press-only buttons.
  - **`topic`** — exact topic matched against incoming messages.
  - **`valueField`** — JSON path-lite to the numeric value (e.g. `value` or `sensor.value`); `null` to use a bare payload.
  - **`unitField`** — JSON path-lite to a unit string; `null` if none.
  - **`labelField`** — JSON path-lite to a live string for the `{label}` token (e.g. a `"22/18"` cur/target string); `null` if none.
  - **`expectsRetained`** — `true` renders a placeholder until the first value arrives (set the retain flag on the publisher).
  - **`stalenessTimeout`** — `TimeSpan` (`hh:mm:ss`); after this silence the button is dimmed. `null` = never stale.
- **`rules`** — ordered conditional rules over the extracted numeric value; **first match wins**. Each rule may override `backgroundColour` (`#RRGGBB`) and/or `icon`.
  - Condition grammar: `">N"`, `">=N"`, `"<N"`, `"<=N"`, `"==N"`, `"between:A:B"`.
- **`gestures`** — map of gesture name to an ordered action list. `Tap` is the only gesture at launch; actions fire in order.
  - `{ "type": "Publish", "topic": "...", "payload": "..." }` — emit an MQTT message (payload sent verbatim).
  - `{ "type": "Navigate", "targetPageId": "..." }` — switch the active page on the device.

---

## 6. Icon system

Icons are referenced by string in `display.baseIcon` and in a rule's `icon` field. All 7,400+
**Material Design Icons** are available as `builtin:<mdi-name>` — for example `builtin:thermometer`,
`builtin:lightbulb`, `builtin:lightbulb-outline`, `builtin:chevron-right`, `builtin:robot-vacuum`.
Browse the full set and find names at <https://pictogrammers.com/library/mdi>. See
`docs/icon-vocabulary.md` for the project's naming conventions.

To use your own artwork, upload a PNG or JPEG (max 512 KB) with
`POST /devices/{serial}/images` (`multipart/form-data`). The response returns a reference like
`{ "ref": "custom:my-icon.png" }`; use that string as `display.baseIcon`. Uploaded files are stored
under `/data/images/<serial>/` and can be listed or removed via the image endpoints. The icon
resolver tries built-in, then custom, then a fallback — it never throws.

---

## 7. MQTT integration

### Inbound (display updates)

- The service holds **one** wildcard subscription (`Mqtt__TopicPrefix`, default `home/#`). Changing
  button config never touches the broker — topic dispatch happens in-process. Anything not matched by
  a button binding is silently dropped.
- Each button's `inbound.topic` is matched exactly against incoming messages.
- **JSON field extraction** uses dot-path notation. For a payload `{"state": {"temperature": 21.4}}`,
  set `"valueField": "state.temperature"`.
- Preferred payload shape: `{"value": 1023, "unit": "ppm", "ts": "2026-06-05T12:00:00Z"}`. Bare numeric
  payloads (e.g. `"1023"`) also work when `valueField` is `null`.
- Publishers should set the **retain** flag so a button populates immediately on service start.
- The matched numeric value is run through the ordered `rules` (first match wins) to pick colour/icon.

### Outbound (button press)

- **`Publish`** action — emits an MQTT message to `topic` with `payload` sent verbatim. Choose a stable
  payload per button (`"true"`, `"toggle"`, or a JSON string).
- **`Navigate`** action — changes the active page on that device to `targetPageId`.

### Home Assistant example

The service speaks plain MQTT — Home Assistant is just one possible publisher/subscriber. To feed a
button, publish (retained) to a topic the service is listening on:

```yaml
# Home Assistant automation: mirror a sensor onto a Stream Deck button
automation:
  - alias: "Publish living-room temperature to Stream Deck"
    trigger:
      - platform: state
        entity_id: sensor.living_room_temperature
    action:
      - service: mqtt.publish
        data:
          topic: "home/living/temperature"
          retain: true
          payload: >-
            {"value": {{ states('sensor.living_room_temperature') }}, "unit": "C"}
```

To act on a button press, add an HA automation triggered by the topic the button's `Publish` action
targets (e.g. `home/office/desk-light/set`).

---

## 8. REST API quick reference

All endpoints require the `X-Api-Key` header except `/health` and `/openapi.json`. A missing or wrong
key returns `401`. See `docs/api-guide.md` for full request/response shapes and worked examples.

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/health` | No | Liveness check — `{"status":"healthy"}`. |
| GET | `/openapi.json` | No | Full OpenAPI 3 schema. |
| GET | `/devices` | Yes | List catalogued devices (serial, model, key geometry, connection state). |
| GET | `/devices/{serial}/status` | Yes | Connection state for one device. |
| POST | `/devices/{serial}/force-render` | Yes | Rebuild desired state from config and full clear-and-redraw the current page. |
| GET | `/devices/{serial}/active-page` | Yes | Current page + available navigation targets. |
| POST | `/devices/{serial}/navigate` | Yes | Force navigation to a page (re-renders, clears stale keys). `400` unknown/empty page, `404` no config. |
| GET | `/devices/{serial}/config` | Yes | Read the stored config for a device. |
| PUT | `/devices/{serial}/config` | Yes | Replace the full config (validated). On success rebuilds the projection, clears+redraws all keys, resets to the first page (`?resetPage=false` to keep the current page). `204` / `400`. |
| POST | `/config/upgrade` | Yes | Migrate a config JSON to the current schema version. Does not persist. `422` if unsupported. |
| GET | `/devices/{serial}/images` | Yes | List custom icons for a device. |
| POST | `/devices/{serial}/images` | Yes | Upload a custom icon (PNG/JPEG, max 512 KB). Returns `{"ref":"custom:..."}`. |
| DELETE | `/devices/{serial}/images/{filename}` | Yes | Delete a custom icon. |

Common error codes: `400` validation (`errors` array), `401` bad/missing key, `404` unknown serial or
no config, `422` schema too old to upgrade.

---

## 9. Development

```bash
dotnet build                                   # build the solution
dotnet test                                    # run all tests
dotnet test --filter "FullyQualifiedName~SomeTest"   # run a single test
dotnet run --project src/StreamDeckPilot.Api   # run locally (dev machine with a device attached)
docker build -f src/StreamDeckPilot.Api/Dockerfile -t stream-deck-pilot .   # build the Linux container
```

The runnable host is `src/StreamDeckPilot.Api`. Logs are structured JSON to stdout (Serilog). For
architecture details see `CLAUDE.md`; for the full design contract see `streamdeck-service-spec.md`.
