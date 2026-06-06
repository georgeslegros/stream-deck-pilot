# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# context-mode — MANDATORY routing rules

You have context-mode MCP tools available. These rules are NOT optional — they protect your context window from flooding. A single unrouted command can dump 56 KB into context and waste the entire session.

## BLOCKED commands — do NOT attempt these

### curl / wget — BLOCKED
Any Bash command containing `curl` or `wget` is intercepted and replaced with an error message. Do NOT retry.
Instead use:
- `ctx_fetch_and_index(url, source)` to fetch and index web pages
- `ctx_execute(language: "javascript", code: "const r = await fetch(...)")` to run HTTP calls in sandbox

### Inline HTTP — BLOCKED
Any Bash command containing `fetch('http`, `requests.get(`, `requests.post(`, `http.get(`, or `http.request(` is intercepted and replaced with an error message. Do NOT retry with Bash.
Instead use:
- `ctx_execute(language, code)` to run HTTP calls in sandbox — only stdout enters context

### WebFetch — BLOCKED
WebFetch calls are denied entirely. The URL is extracted and you are told to use `ctx_fetch_and_index` instead.
Instead use:
- `ctx_fetch_and_index(url, source)` then `ctx_search(queries)` to query the indexed content

## REDIRECTED tools — use sandbox equivalents

### Bash (>20 lines output)
Bash is ONLY for: `git`, `mkdir`, `rm`, `mv`, `cd`, `ls`, `npm install`, `pip install`, and other short-output commands.
For everything else, use:
- `ctx_batch_execute(commands, queries)` — run multiple commands + search in ONE call
- `ctx_execute(language: "shell", code: "...")` — run in sandbox, only stdout enters context

### Read (for analysis)
If you are reading a file to **Edit** it → Read is correct (Edit needs content in context).
If you are reading to **analyze, explore, or summarize** → use `ctx_execute_file(path, language, code)` instead. Only your printed summary enters context. The raw file content stays in the sandbox.

### Grep (large results)
Grep results can flood context. Use `ctx_execute(language: "shell", code: "grep ...")` to run searches in sandbox. Only your printed summary enters context.

## Tool selection hierarchy

1. **GATHER**: `ctx_batch_execute(commands, queries)` — Primary tool. Runs all commands, auto-indexes output, returns search results. ONE call replaces 30+ individual calls.
2. **FOLLOW-UP**: `ctx_search(queries: ["q1", "q2", ...])` — Query indexed content. Pass ALL questions as array in ONE call.
3. **PROCESSING**: `ctx_execute(language, code)` | `ctx_execute_file(path, language, code)` — Sandbox execution. Only stdout enters context.
4. **WEB**: `ctx_fetch_and_index(url, source)` then `ctx_search(queries)` — Fetch, chunk, index, query. Raw HTML never enters context.
5. **INDEX**: `ctx_index(content, source)` — Store content in FTS5 knowledge base for later search.

## Subagent routing

When spawning subagents (Agent/Task tool), the routing block is automatically injected into their prompt. Bash-type subagents are upgraded to general-purpose so they have access to MCP tools. You do NOT need to manually instruct subagents about context-mode.

## Output constraints

- Keep responses under 500 words.
- Write artifacts (code, configs, PRDs) to FILES — never return them as inline text. Return only: file path + 1-line description.
- When indexing content, use descriptive source labels so others can `ctx_search(source: "label")` later.

## ctx commands

| Command | Action |
|---------|--------|
| `ctx stats` | Call the `ctx_stats` MCP tool and display the full output verbatim |
| `ctx doctor` | Call the `ctx_doctor` MCP tool, run the returned shell command, display as checklist |
| `ctx upgrade` | Call the `ctx_upgrade` MCP tool, run the returned shell command, display as checklist |

---

# Project: Stream Deck Pilot

A headless .NET (C#) service that drives Elgato Stream Deck devices from a Docker container on a Ubuntu mini server, controlled via REST API and reacting to MQTT events. The authoritative spec is `streamdeck-service-spec.md`.

## Technology stack

- **Runtime:** .NET (current LTS), ASP.NET Core minimal API
- **Background work:** `BackgroundService` / `IHostedService` (device supervision, MQTT)
- **Stream Deck:** `StreamDeckSharp` + `OpenMacroBoard.SDK`
- **MQTT client:** MQTTnet
- **Serialisation:** `System.Text.Json` (shared between API wire format and persisted files)
- **Observability:** OpenTelemetry API → OTLP; structured logs to stdout
- **Deployment:** single Docker container, `linux-x64`, behind Traefik (HTTPS termination)

## Commands

Once the project is scaffolded under a solution file, standard .NET commands apply:

```bash
dotnet build                        # build
dotnet test                         # all tests
dotnet test --filter "FullyQualifiedName~SomeTest"   # single test
dotnet run --project src/StreamDeckPilot             # run locally (Windows dev, device attached)
docker build -t stream-deck-pilot .                  # build Linux container
```

The container requires `libhidapi-libusb0` + `libusb` and the hidraw device mounted at runtime:
```bash
docker run --device /dev/hidraw0 stream-deck-pilot
```

## Architecture

### Key separation of concerns

**Config plane vs. data plane** — Configuration (what tiles exist, rules, thresholds) is managed via REST API and persisted. The runtime only reacts to MQTT events and applies the configured rules. These are never coupled.

**Desired state vs. device state** — The app maintains the intended state of every button in memory at all times. Rendering to physical hardware is a best-effort projection. Device disconnects are non-events; on reconnect, the app re-renders from desired state.

**Home Assistant is transparent** — The app speaks MQTT topics only. No HA token, no HA concepts in app code. HA-specific logic lives in HA automations.

### Domain model

- **Device catalogue** (`catalog.json`) — populated only by physical discovery, never via API. Authority for what serials may be configured.
- **Per-device config** (`config/<serial>.json`) — one file per serial. A connected device with no config renders blank.
- **Pages** — typed with a discriminator field (`ButtonGrid` is the only type at launch). Never model a page as a flat list of buttons; the discriminator is load-bearing for future page types (spanned chart).
- **Buttons** — each has: stable human-readable ID, position (page + key index), display spec, optional inbound MQTT binding (topic + JSON field extraction + staleness timeout), ordered conditional rules (first-match-wins, over extracted numeric value), and a gesture → action-list map (`Tap` only at launch; structure admits `LongPress`/`DoublePress` without schema change).
- **Actions** — discriminated union via `System.Text.Json` polymorphic serialisation: `Publish` (emit MQTT) and `Navigate` (change active page).

### Data-flow pipeline (inbound MQTT → render)

`receive → extract field → evaluate rules → format value → compose label → render to device`

This order is fixed. Rules operate on the numeric value, so extraction always precedes rule evaluation.

### Persistence

- File-based JSON on a mounted Docker volume. No database.
- **Atomic writes:** write to temp file → rename over target (crash-safe). Never overwrite in place.
- Every persisted file carries a `schemaVersion` field. Migrations are chained (v1→v2→v3 in sequence). An upgrade endpoint (`POST`) accepts any supported version and returns it migrated to current.
- Catalogue and config are separate stores with separate files — a config write can never corrupt the catalogue.

### Resilience contracts

- The app reaches "running and serving API" even if the MQTT broker is unreachable at startup. Broker-unavailable is a degraded metric state, not a crash condition.
- Per-device connection state machine: `Disconnected → Connecting → Connected → Faulted → reconnecting`. Use `ConnectionStateChanged` events; do not infer state from write failures.
- Multiple devices are supervised independently.

### Security

- Static API-key middleware on all endpoints (long random token in a header; 401 otherwise).
- All secrets (MQTT credentials, API key, OTLP endpoint) via environment variables / Docker secrets — never in persisted config files or the image.

### Build order (from spec §12)

1. Device spike — prove USB enumeration + render under Linux/Docker (do this first).
2. Core domain model + persistence.
3. REST API + validation.
4. Device supervision + desired-state runtime.
5. MQTT consumption + data-flow pipeline.
6. Outbound publish + navigation actions.
7. Icons (built-in + custom), staleness, formatting.
8. Observability (OTLP + structured stdout logs).
9. Schema migration + upgrade endpoint.

### Future features (design for, do NOT build)

Spanned page (image/chart across full grid), live chart (last-N values), long-press/double-press gestures. The model already accommodates these via the page-type discriminator, the gesture-keyed action map, and the "last value" per button.
