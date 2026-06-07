# Stream Deck Service — Build Brief

**Purpose:** Implementation specification for a headless, containerised service that drives one or more Elgato Stream Deck devices from a permanently-on Ubuntu mini server, controlled via a REST API and driven by MQTT events. This document captures all architectural decisions reached during design. It is the authoritative brief for implementation.

**Target implementer:** Claude Code.

---

## 1. Overview & Goals

Build a .NET (C#) service, deployed as a single Docker container on a headless Ubuntu mini server, that:

- Owns the USB connection to one or more Elgato Stream Deck devices and renders content to their keys.
- Exposes a thin REST API for configuration and control (the primary remote-control surface; designed to be driven by Claude Code or scripts).
- Reacts to MQTT messages to update key state (values, colours, icons) according to user-defined rules.
- Publishes MQTT messages on key presses (e.g. to trigger Home Assistant automations).
- Persists its configuration to disk so it survives restarts.
- Is cleanly removable: deleting the container plus its mounted state volume leaves nothing behind on the host.

### Core design principles

- **One stable contract per concern, swappable implementation behind it.** The app depends on abstractions (an MQTT broker, an OTLP endpoint, a device interface), never on specific downstream systems.
- **Home Assistant is transparent.** The app knows nothing about HA. It speaks MQTT topics only. Anything HA-specific lives in HA automations, not in this app.
- **Config plane vs. data plane are separate.** Configuration (what tiles exist, what they show, thresholds) is managed via the API and persisted. The runtime merely reacts to events and applies the configured rules.
- **Desired state vs. device state are separate.** The app always maintains the intended state of every button. Rendering to physical hardware is a best-effort projection applied whenever a device is present. This separation is what makes device disconnects a non-event.

---

## 2. Technology & Platform

- **Language/runtime:** .NET (C#), current LTS.
- **Hosting model:** ASP.NET Core minimal API + `BackgroundService` / `IHostedService` for long-running work (device supervision, MQTT consumption).
- **Deployment:** Docker container, target `linux-x64` (confirm whether the mini server is x64 or arm64 and set the publish RID accordingly).
- **Stream Deck library:** `StreamDeckSharp` + `OpenMacroBoard.SDK`. Linux is officially supported by the maintainer (tested on Debian/Ubuntu). Current release supports the MK.2.
- **MQTT client:** any maintained .NET MQTT library (e.g. MQTTnet).
- **Cross-platform dev:** development on Windows (or any dev box) with the physical device attached is fully supported; the app is portable and only the native HID layer differs by OS. Publish a Linux container for deployment.

### Native dependencies in the container

The container image MUST include the native HID library the Stream Deck library rides on:
- `libhidapi-libusb0` (and libusb).
- The host needs a udev rule granting access to the Stream Deck (USB vendor ID `0fd9`), and the container must run with the relevant hidraw device mounted. Document the exact `docker run` / compose device-mount + `--privileged` (or a tighter device cgroup rule) needed.

---

## 3. Target Hardware

- Primary device: **Elgato Stream Deck MK.2 (Scissor Switch revision)** — 5×3 = 15 keys.
- The MK.2 is supported by the chosen library. **Caveat:** Elgato sometimes changes USB product IDs on hardware revisions without renaming the product. If `OpenDevice()` does not enumerate the unit, the most likely cause is an unrecognised product ID for this revision.
  - **Action if so:** capture the USB descriptor (`lsusb -v`; vendor `0fd9`, note the product ID and product string), check the library's GitHub for an existing issue/PR, and if absent, submit a small PR registering the new product ID against the existing MK.2 device definition. Verify image rendering and key events still behave (scissor-switch is expected to be a mechanical change only, but confirm the protocol matches before assuming a trivial fix).

### Spike (do this FIRST, before building the application)

Minimal C# console app in a Linux container that:
1. Enumerates the Stream Deck via `StreamDeckSharp` with `libhidapi` present and the hidraw device mounted.
2. Calls `SetBrightness` and lights up one key.

This proves the only thing that can't be validated off-device: the USB path under Linux + Docker. If it enumerates, the rest is application code. If it doesn't, resolve the product-ID issue (above) before proceeding.

---

## 4. Domain Model

### 4.1 Device catalogue (discovery-gated, app-owned, persisted)

- The app maintains a **persisted catalogue of every device it has ever seen**, keyed by **serial number**.
- The catalogue is populated **only by discovery** — i.e. when a physical device connects. It is **never** written via the API.
- Each catalogue entry stores: serial, model, key geometry (rows × cols / key count), firmware/serial details if exposed, first-seen and last-seen timestamps.
- The catalogue is the **authority for what may be configured**: a config write for a serial is accepted only if that serial exists in the catalogue.
- The catalogue persists across restarts and across device disconnection (a known-but-unplugged device stays in the catalogue).

### 4.2 Device states (exposed via API)

- **Known + connected** — seen before, plugged in now. Configurable and renderable.
- **Known + disconnected** — seen before, not currently plugged in. Configurable (config persists, applied on reconnect); not currently renderable.
- **Unknown** — never seen. Not configurable. Only resolved by plugging it in.

### 4.3 Configuration (per-device, API-managed, persisted)

- Configuration is **scoped per device** and keyed by serial.
- Config writes are **per-device** (one serial per write) to avoid large payloads and to allow updating one device without touching another.
- Config persists independently of device presence (you can hold config for a device that is currently disconnected; it applies when the device returns).
- A connected device with **no config renders blank** (keys off / low brightness). This is the "seen you, waiting for config" state.

### 4.4 Pages

- A device's config contains one or more **pages**. Only one page is active at a time.
- **A page has a type/mode discriminator.** Today the only type is `ButtonGrid`. (This discriminator exists from day one to admit future page types such as a spanned image/chart across the whole grid — see §11. Do not model a page as *only* a flat collection of buttons.)
- Navigation between pages is performed by buttons with a navigation action (see §4.6).

### 4.5 Button — display & inbound

Each button (within a page) carries:

- **Identity:** a stable, human-friendly ID used to reference it via the API (e.g. `office-co2`). This is an identifier, NOT a binding mechanism.
- **Position:** page + key index. Must be valid for *that device's* geometry.
- **Display spec:** how the tile looks, expressed as **user-chosen** elements — the layout is never inferred from the *kind* of data the tile carries:
  - **Icon** (built-in or custom) with an explicit **placement**: a small corner accent, or a large centred hero. (When a hero text zone is also present, the text owns the centre.)
  - **Text zones** — a large centred **hero** zone and a small **bottom caption** zone. Each zone has a static **label** *and* an optional **template** resolved from the incoming value / unit / live-label. When no value has arrived yet there is nothing to resolve against, so the static label is shown as the fallback (this also serves as the "before first value" placeholder). The renderer composes whatever zones are filled plus the icon placement — it does not pick a layout from the data type.
- **Inbound binding (optional):** present only for data-driven buttons. Contains:
  - **Topic** — the MQTT topic this button listens to. (The app never knows or cares that HA may be the source. Binding is by topic only.)
  - **Field extraction** — if the payload is JSON, which field is the value and which (optionally) is the unit/metadata. Support a simple field-name / JSON-path-lite selector.
  - **Retained/initial behaviour** — an explicit per-button flag stating the button expects a retained value, plus what to display before any value has arrived (placeholder / dimmed) so an unpopulated tile does not look broken.
  - **Staleness timeout (optional):** a per-button duration. If no value arrives within the timeout, the button degrades **gently** (dim / greyed) to indicate stale data — not an alarm state. Evaluated against "time since last value received." No timeout configured ⇒ never goes stale. Requires the runtime to keep a per-button last-updated timestamp.
- **Conditional rules:** an **ordered** list of conditions over the **extracted numeric value**, mapping value ranges/thresholds to visual changes (background colour, icon swap). First-match-wins. (Example: CO2 > 1000 ⇒ red, else green.)

### 4.6 Button — press behaviour (gesture-keyed, action list)

- A button's behaviour is modelled as a **map of gesture → ordered list of actions**.
  - Today the only gesture key is `Tap`. (Model it as a gesture-keyed structure from the start so `DoublePress` / `LongPress` become data, not a schema change — see §11.)
- Each gesture maps to an **ordered list of actions** (future-proofs "do more than one thing on press").
- Actions are a **discriminated union** (polymorphic, with a `type` discriminator; use `System.Text.Json` polymorphic serialisation). Action types at launch:
  - **Publish** — publish to an MQTT topic with a payload. Payload may be fixed (e.g. a toggle) or parameterised (e.g. set thermostat to 20°).
  - **Navigate** — change the active page. Carries a target page reference (must resolve within the *same device's* page set). No MQTT involved.
- Actions in a list execute in sequence, fire-and-forget (e.g. publish then navigate). Confirm this ordering semantic is acceptable; no stop-on-failure required at launch.

### 4.7 Icons / images

- The app ships a **built-in icon library** — the full Material Design Icons set, referenced by MDI name (`builtin:<mdi-name>`).
- Users may also supply **custom images**.
- A button's icon reference must distinguish built-in vs. custom (namespacing convention).
- Custom images are **persisted state**: stored on the mounted volume alongside config, so they survive restarts and are removed with the rest of the state when the container/volume is deleted.
- A button referencing a missing custom image must **degrade gracefully** (fallback icon), never fail to render.

### 4.8 Validation (enforced at the API boundary, on write)

Reject invalid config with clear errors; do not store-then-fail-at-render. Validate at minimum:
- Target serial exists in the catalogue.
- Key positions are within the device's geometry; no two buttons share the same page+index.
- Navigation targets resolve to an existing page within the same device.
- (Recommended) warn on orphan pages unreachable by any navigation button.

---

## 5. Data-Flow Pipeline (per inbound MQTT message)

Fixed order — this is the core data flow and is identical regardless of device/page:

1. **Receive** message on a subscribed topic.
2. **Extract** fields from the payload (value, unit, metadata).
3. **Evaluate** the ordered conditional rules against the extracted **numeric** value → select colour/icon.
4. **Format** the value to display text (precision, rounding).
5. **Compose** the label from value + unit + static text via the button's format template.
6. **Render** onto the device key (best-effort; only if the device is connected).

Notes:
- Keep the **format spec constrained** (named fields, precision, simple template string like `"{value} {unit}"`). Do **not** embed an arbitrary expression language at launch. If real logic is needed later, revisit then.
- Rules operate on the numeric value, so extraction must precede both rule evaluation and formatting.

---

## 6. MQTT Contract

- **Broker:** existing RabbitMQ in the home lab with the MQTT plugin enabled (recent version, MQTT already active). The app connects as an MQTT client.
- **Topic naming:** a deliberate hierarchy (e.g. `home/<room>/<metric>`, `home/streamdeck/<button>`); design to allow wildcard subscriptions later (e.g. `home/+/co2`).
- **Inbound (values → app):** the app subscribes to the topics its buttons bind to. Payloads SHOULD be **structured JSON** (`{"value": ..., "unit": "...", "ts": "..."}`) so messages are self-describing; the binding's field-extraction selects the value/unit. Bare-value payloads are also supportable but less self-describing.
- **Outbound (presses → broker):** a Publish action emits to a configured topic with a configured payload. **Pin one stable convention** for the outbound topic + JSON payload shape so the HA side has a fixed contract to write automations against (discuss exact shape during implementation — see open items).
- **Retained messages:** publishers (e.g. HA) SHOULD set the retain flag for value topics so that on app start / reconnect the latest value is delivered immediately and tiles populate without waiting for the next change.
- **Broker auth & permissions:** the app authenticates to RabbitMQ with MQTT credentials (injected as secrets — see §8). Restrict that MQTT user's publish/subscribe to only the topics it needs (`home/streamdeck/#` plus the sensor topics) to contain blast radius.
- **Trust boundary:** the app authenticates to the **broker only**, never to HA. The broker is the sole shared trust point. The app holds **no HA token**.

---

## 7. Runtime & Resilience

### Device supervision
- Each device is an **independently supervised connection** with an explicit state machine: `Disconnected → Connecting → Connected → (Faulted) → reconnecting…`.
- Use the library's `ConnectionStateChanged` event to detect disconnects; do not rely on a failed write throwing.
- While a device is disconnected the app **keeps running**: keep consuming MQTT, keep evaluating rules, keep the **last known intended state** per button in memory.
- On reconnect, **re-render everything from desired state** (combined with retained MQTT values) so the device returns showing current data, not blank.
- Writes to an absent device **no-op gracefully** (or queue); they never throw up the stack.
- Multiple devices are supervised independently — one unplugged device never affects another.

### Broker resilience
- On startup the app **reaches "running and serving its API" even if the broker is not yet reachable.** Broker-unavailable is a **degraded state surfaced via metrics**, not a barrier to starting.
- The app **waits and retries** the broker connection (with backoff); it does not crash-loop. The config API and device rendering remain available while the broker is down; only value updates (data plane) wait.

### General
- Unplugging a device, a device reboot, a transient USB hiccup, or a broker outage are all **normal, recoverable states**, never fatal.

---

## 8. Security

- **Transport:** the service sits behind **Traefik**, which terminates **HTTPS**. On-wire encryption is handled there.
- **Authentication:** a **static API key / shared secret** on all state-changing (and ideally all) API endpoints — a long random token sent in a header, checked by middleware; reject with 401 otherwise. No user store, no token issuance. (Traefik may additionally enforce it before traffic reaches the app; app-side check is still required as defence in depth.)
- **Network posture:** prefer binding the app so it is only reachable via Traefik; do not expose the raw port broadly. Keep the API-key check regardless of binding, so changing the exposure never silently opens an unauthenticated control plane.
- **Secrets:** all downstream credentials (RabbitMQ/MQTT creds, the API key, the OTLP endpoint if sensitive) are injected via **environment variables / Docker secrets / mounted files** — never baked into the image or committed to source, never written into the persisted config files.
- **Out of scope** (single-user home service): multi-user auth, RBAC, mTLS, identity systems.

---

## 9. Persistence / Storage

- **File-based JSON on a mounted volume.** No database. The data is a handful of small documents read whole at startup and rewritten whole on edit; there are no queries, no joins, a single writer.
- **Layout:**
  - Device catalogue: its own JSON file (e.g. `catalog.json`). App-owned, append-mostly.
  - Per-device config: one JSON file per serial (e.g. `config/<serial>.json`). User-edited via API.
  - Custom images: stored on the same volume.
  - **Keep catalogue and config as separate stores** — different lifecycles; a config edit must never be able to corrupt the catalogue.
- **Atomic writes:** write to a temp file then atomically rename over the target (same filesystem). This makes writes crash-safe. Do not overwrite files in place.
- **Single-writer discipline:** funnel config writes through one component to serialise them (writes are rare and single-user).
- **Serialisation:** use `System.Text.Json`. The stored format and the API wire format can share the same model shape to avoid a mapping layer.
- **Container hygiene:** all persistent state lives on the mounted volume; `docker rm` + deleting the volume leaves nothing on the host.

### Schema versioning & migration
- Every persisted config file carries a **`schemaVersion`** field.
- Implement a **chained migration** model (v1→v2→v3 applied in sequence), not pairwise direct migrations.
- Define a **support floor**: below some version the app refuses to load and instructs the user to upgrade via an older release. (The app will not support every historical version forever.)
- The same migration code serves two callers:
  1. **On-load upgrade** — old files on disk are migrated to current on read.
  2. **Explicit upgrade endpoint** — `POST` a config of any supported version, receive it transformed to the current version (so a client, e.g. a laptop holding a v1, can ask the API to convert it to v2). See §10.

---

## 10. REST API (surface)

The primary remote-control and configuration surface. Designed to be driven by Claude Code / scripts. All endpoints behind the API-key check.

Required capabilities:
- **List devices** (the catalogue) with their specs (model, geometry) and current connection state. This lets a client discover what it may configure and the valid key grid before writing.
- **Get device status** — surface the per-device connection state machine.
- **Get / set per-device config** — write is per-device (one serial), validated per §4.8. Config for a non-catalogued serial is rejected.
- **Upgrade config** — accept a config of any supported schema version and return it migrated to current (§9).
- **Debug/control helpers** (handy, low-risk): force re-render of a device/page; "test this button" (render or trigger a button without a real MQTT message). Exact set to be finalised during implementation.

Open items to settle with the implementer:
- Exact outbound publish payload convention (topic + JSON shape) — §6.
- Exact debug/control endpoint set.

---

## 11. Observability

- **App-side contract: emit OpenTelemetry over OTLP to a single configurable endpoint** (endpoint via env var, per the container model), **plus structured logs to stdout**. That is the entire app-side requirement. The app depends only on the existence of an OTLP target.
- **Instrument against the OpenTelemetry API** (not a backend-specific client) so the exporter/backend is swappable.

### Deployment topology (infrastructure, not app code)
- **Now:** `App → Alloy (single OTLP front door) → Prometheus (metrics storage) → existing Grafana`.
  - **Grafana Alloy** is the one required target from the app's perspective. It receives OTLP and routes signals. (Alloy is a pipe/router, not a store — it does not retain data and Grafana cannot query it directly.)
  - **Prometheus** stores metrics; **Grafana** (already present) reads from Prometheus.
  - **Logs:** the app emits them via OTLP to Alloy (currently routed nowhere / dropped) **and** writes structured logs to its own **stdout** (configure Docker's `json-file` driver with rotation so stdout logs survive restarts; note plain `docker logs` is lost on container *recreation*, hence the deferred Loki path).
  - **Traces:** emitted to Alloy, currently dropped (no Tempo yet).
- **Later, independently, if needed (config-only, no app change):**
  - **Loki** behind Alloy for searchable logs in Grafana.
  - **Tempo** behind Alloy for traces in Grafana.
  - Adding either = add the container, add the Alloy route, add the Grafana datasource. The app already emits the data.
  - Loki and Tempo are independent additions and may be deployed separately.

### What to instrument (meaningful operation)
- **Metrics (the headline operational view):**
  - **Per-device connection state** (gauge) — the single most useful signal; enables "device X disconnected for N minutes" + alerting.
  - MQTT messages consumed (rate) and count of unparseable/dropped messages.
  - Render operations (count) and render failures.
  - Button presses (count, by device/button).
  - Reconnect events per device (frequent reconnects ⇒ flaky USB/power).
  - Broker connection state (degraded-on-startup visibility — §7).
- **Logs (to stdout now, Loki later):** structured, with attributes (device serial, topic, button id) — never string-interpolated blobs. Warnings: malformed payload, config referencing absent device, failed publish, value matching no extraction field. Errors: device write failure, config-load failure, MQTT connection loss.
- **Traces (light touch):** one span covering the inbound message pipeline (extract → evaluate → render). Do not over-invest in tracing beyond this.

**Discipline that makes deferral work:** keep logs and traces well-structured **now**, even though their backends don't exist yet, so that plugging in Loki/Tempo later yields useful, queryable data. Cross-signal correlation (metric spike → jump to that service's logs at that moment) is the payoff of routing everything through one OTLP front door.

---

## 12. Suggested Build Order

1. **Device spike** (§3) — prove USB enumeration + render under Linux/Docker. Resolve product-ID issue if needed (and contribute a PR upstream).
2. **Core domain model + persistence** (§4, §9) — catalogue, per-device config, JSON storage with atomic writes and `schemaVersion`. Testable with no hardware.
3. **REST API + validation** (§10, §4.8) — config CRUD, device listing/status, behind API-key auth.
4. **Device supervision + desired-state/device-state runtime** (§7) — using the virtual board (OpenMacroBoard `SocketIO` / `VirtualMacroBoard`) for hardware-free testing, and the real MK.2 for render-fidelity checks.
5. **MQTT consumption + the data-flow pipeline** (§5, §6) — inbound values → rules → render; testable against a mock/real broker, no deck required.
6. **Outbound publish + navigation actions** (§4.6) — presses → broker; page navigation.
7. **Icons (built-in + custom), staleness, formatting** (§4.5, §4.7).
8. **Observability** (§11) — OTLP export + structured stdout logs; stand up Alloy + Prometheus + Grafana.
9. **Schema migration + upgrade endpoint** (§9, §10).

### Independent test seams (most of the app needs no hardware)
- **Pure logic** (rule evaluation, config parsing, value→state mapping): plain unit tests, no device, no broker.
- **Device seam:** real-device spike (rendering/enumeration) + virtual board (logic-level end-to-end).
- **Queue seam:** real RabbitMQ container or a mock.
- The physical MK.2 is reserved for the two things only it can answer: does it enumerate under Linux/Docker, and does rendering look right on real keys.

---

## 13. Future Features (DESIGN FOR, do NOT BUILD)

These are explicitly **out of scope** for v1. They are listed because the model has been shaped to *admit* them without a rewrite. Do not implement; do not let the model foreclose them.

- **Spanned page — chart or photo across the whole grid.** A page rendered as a single canvas sliced across all keys, rather than independent buttons. **Forethought already in the model:** pages carry a type/mode discriminator (§4.4); this becomes a new page type, not a model upheaval.
- **Live chart.** Generate an image from a value series, slice across keys, refresh on new data. Rides the existing inbound pipeline. **Forethought:** the runtime currently keeps "last value" per button; a live chart wants "last N values" — retained short history is the seed. Do not build, but be aware.
- **Special button gestures — long press / double press.** **Forethought already in the model:** button behaviour is a map of gesture → action list (§4.6) with only `Tap` populated today. New gestures become data, not schema surgery.

**Principle:** don't build these, but keep the model's shape able to admit them — page-type discriminator, gesture-keyed actions, and awareness that "last value" may become "last N." These small generalisations turn three future rewrites into three future additions.

---

## 14. Decision Log (quick reference)

| Area | Decision |
|---|---|
| Language / stack | .NET (C#), ASP.NET Core minimal API + hosted services |
| Deployment | Single Docker container, Linux, behind Traefik (HTTPS) |
| Stream Deck lib | StreamDeckSharp + OpenMacroBoard.SDK (Linux supported; MK.2 supported) |
| Device | Elgato Stream Deck MK.2 Scissor Switch (15 keys); verify product ID, PR upstream if unrecognised |
| Multi-device | Supported; config + runtime keyed by serial; independent supervision |
| Discovery | Config only allowed for devices seen via discovery; catalogue is app-owned & persisted |
| Unconfigured device | Renders blank |
| Config scope | Per-device writes (one serial), validated at API boundary |
| Pages | Typed (discriminator); `ButtonGrid` only at launch |
| Button inbound | Bind by MQTT topic only (HA transparent); JSON field extraction; optional retained + staleness |
| Button rules | Ordered, first-match-wins, over extracted numeric value |
| Button press | Map of gesture → ordered action list; actions = discriminated union (Publish, Navigate) |
| Icons | Built-in library + custom (persisted on volume); graceful fallback |
| Data pipeline | receive → extract → evaluate rules → format → compose → render |
| MQTT broker | Existing RabbitMQ + MQTT plugin; app authenticates to broker only, no HA token |
| Payloads | Structured JSON preferred; retain flag for value topics |
| Storage | File-based JSON on mounted volume; atomic temp-write-then-rename; single writer; no DB |
| Schema versioning | `schemaVersion` per file; chained migrations; support floor; upgrade endpoint |
| Security | Static API-key middleware on all endpoints; HTTPS via Traefik; secrets via env/Docker secrets |
| Device resilience | Per-device state machine; degrade-don't-die; re-render from desired state on reconnect |
| Broker resilience | Start & serve API even if broker down; wait + retry; surface as degraded metric |
| Observability (app) | Emit OTLP to one configurable endpoint + structured stdout logs; instrument vs. OTel API |
| Observability (infra) | Now: Alloy + Prometheus + Grafana. Later (config-only): Loki (logs), Tempo (traces), independently |
| Out of scope | Multi-user/RBAC/mTLS; DB; HA awareness in app; building the §13 future features |

---

*End of brief.*
