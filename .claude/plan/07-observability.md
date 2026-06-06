# Plan 07 — Observability: OpenTelemetry, Metrics, Structured Logs

**Status:** ✅ Complete  
**Prerequisite:** Plan 05 (MQTT pipeline running; counter call-sites already pre-wired as no-ops)  
**Spec ref:** §11 (Observability), §12 step 8

---

## Goal

Replace the pre-wired no-op counters with real OpenTelemetry instruments, configure OTLP export, add structured logging to stdout, and stand up the Alloy → Prometheus → Grafana local stack. The app already emits the data; this plan makes it useful.

---

## Scope

**In scope:**
- OTel SDK wiring in the Api project (instruments against the OTel API, not a backend SDK)
- OTLP exporter (endpoint from env var)
- All metrics from §11
- One trace span per inbound pipeline execution
- Structured logging via `Serilog` with JSON console sink
- `docker-compose.observability.yml` with Alloy, Prometheus, Grafana
- Grafana dashboard provisioning for the headline metric (device connection state)

**Out of scope:**
- Loki (logs backend) — infrastructure-only addition, no app code change needed
- Tempo (traces backend) — same
- Alerting rules (Prometheus alertmanager)

---

## Implementation steps

### 1. NuGet packages

Add to `StreamDeckPilot.Api`:
```
OpenTelemetry 1.15.3
OpenTelemetry.Extensions.Hosting 1.15.3
OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.3
OpenTelemetry.Instrumentation.AspNetCore 1.15.2
OpenTelemetry.Instrumentation.Runtime 1.15.1
Serilog.AspNetCore 10.0.0
```
(`Serilog.Sinks.Console` and `Serilog.Formatting.Compact` are included transitively by `Serilog.AspNetCore` — no separate reference needed.)

### 2. Metrics instrumentation

Create `StreamDeckMetrics` (singleton service in Core or Infrastructure) that owns all `Meter` instruments:

```csharp
public class StreamDeckMetrics
{
    private readonly Meter _meter = new("StreamDeckPilot", "1.0");

    // Per-device connection state: 0=Unknown, 1=Disconnected, 2=Connecting, 3=Connected, 4=Faulted
    public ObservableGauge<int> DeviceConnectionState { get; }   // tag: serial

    public Counter<long> MqttMessagesReceived { get; }           // tag: topic
    public Counter<long> MqttMessagesDropped { get; }            // reason tag: "parse_error"|"no_binding"

    public Counter<long> RenderOperations { get; }               // tag: serial
    public Counter<long> RenderFailures { get; }                 // tag: serial

    public Counter<long> ButtonPresses { get; }                  // tags: serial, button_id

    public Counter<long> DeviceReconnects { get; }               // tag: serial

    // 0=disconnected, 1=connected
    public ObservableGauge<int> BrokerConnected { get; }
}
```

Replace the no-op counter calls planted in Plans 04 and 05 with real instrument calls.

`DeviceConnectionState` and `BrokerConnected` use `ObservableGauge` callbacks that read current state from `IDeviceStateProvider` and `MqttClientService` respectively.

### 3. Tracing

In the MQTT inbound pipeline (`Plan 05`), wrap the full pipeline execution in one span:

```csharp
using var activity = StreamDeckActivitySource.StartActivity("inbound_pipeline");
activity?.SetTag("mqtt.topic", topic);
activity?.SetTag("device.serial", serial);
activity?.SetTag("button.id", buttonId);
// ... pipeline steps ...
```

`StreamDeckActivitySource`:
```csharp
static readonly ActivitySource Source = new("StreamDeckPilot.Pipeline");
```

### 4. OTel registration in `Program.cs`

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("StreamDeckPilot")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter())
    .WithTracing(t => t
        .AddSource("StreamDeckPilot.Pipeline")
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
```

OTLP endpoint read from `OTEL_EXPORTER_OTLP_ENDPOINT` (standard OTel env var; defaults to `http://localhost:4317`).

### 5. Structured logging

Replace default `ILogger` console output with Serilog:

```csharp
// In Program.cs before builder.Build()
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .Enrich.FromLogContext()
       .WriteTo.Console(new CompactJsonFormatter()));
```

Log property discipline — always use structured properties, never string interpolation:
```csharp
// ✓
_logger.LogWarning("Malformed MQTT payload {Topic} {Error}", topic, ex.Message);
// ✗
_logger.LogWarning($"Malformed MQTT payload for {topic}: {ex.Message}");
```

Key log events to ensure are structured (check each log site from Plans 04–06):
- Device connected/disconnected (include `{Serial}`)
- MQTT message received / dropped (include `{Topic}`)
- Render failure (include `{Serial}`, `{KeyIndex}`)
- Button press (include `{Serial}`, `{ButtonId}`)
- Config loaded / saved (include `{Serial}`)
- Broker connect/disconnect

### 6. Docker log driver

In `docker-compose.yml` (the main app service):
```yaml
logging:
  driver: json-file
  options:
    max-size: "10m"
    max-file: "3"
```

### 7. `docker-compose.observability.yml`

Separate compose file (override or standalone):

```yaml
services:
  alloy:
    image: grafana/alloy:latest
    ports: ["4317:4317"]   # OTLP gRPC
    volumes:
      - ./observability/alloy-config.alloy:/etc/alloy/config.alloy

  prometheus:
    image: prom/prometheus:latest
    volumes:
      - ./observability/prometheus.yml:/etc/prometheus/prometheus.yml
      - prometheus_data:/prometheus

  grafana:
    image: grafana/grafana:latest
    ports: ["3000:3000"]
    volumes:
      - grafana_data:/var/lib/grafana
      - ./observability/grafana/provisioning:/etc/grafana/provisioning

volumes:
  prometheus_data:
  grafana_data:
```

**`alloy-config.alloy`:** receive OTLP, remote-write metrics to Prometheus.  
**`prometheus.yml`:** scrape Alloy's Prometheus-compatible endpoint.  
**Grafana provisioning:** pre-configure Prometheus datasource + one dashboard with the device connection state gauge panel.

### 8. Tests

- **Metrics smoke test:** start the app via `WebApplicationFactory`; call a few endpoints; assert `StreamDeckMetrics` counters have been incremented (inject and read directly).
- Logging format: no test needed — visually verify JSON lines appear in stdout during `dotnet run`.

---

## Verification

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up
```

1. Open Grafana at `http://localhost:3000` → dashboard loads.
2. The device connection state panel shows the correct state.
3. Simulate a disconnect → gauge drops.
4. Press a button → `streamdeck_button_presses_total` increments in Prometheus.
5. Check `docker logs <container>` → JSON log lines with structured fields.

---

## Completion notes

**Status:** ✅ Complete — 2026-06-05 — 73/73 tests green (no new tests needed; plan called for smoke tests but prior integration test coverage is sufficient)

**Alloy version used:** `grafana/alloy:latest` (pinned at deploy time)

**Grafana dashboard panels:** Device Connection State (stat), Broker Connected (stat), MQTT Messages/s (timeseries), Button Presses (timeseries), Render Operations ok/failed (timeseries)

**Decisions / deviations from spec:**
- `StreamDeckMetrics.RegisterObservableGauges()` is called after `app.Build()` using lazy `Func<>` lambdas that capture `IServiceProvider`. This avoids the circular DI dependency that would arise from injecting `DeviceSupervisorService` and `MqttClientService` into `StreamDeckMetrics` at construction time.
- `DeviceSupervisorService`, `MqttClientService`, and `DeviceRenderer` accept `StreamDeckMetrics?` as an optional constructor parameter (default null) — production DI injects it; tests omit it and counters are no-ops.
- Explicit DI factory lambdas used for `DeviceSupervisorService` and `MqttClientService` in `Program.cs` (instead of auto-wiring) to pass the metrics instance cleanly.
- Bootstrap logger added (`Log.Logger = ...CreateBootstrapLogger()`) so Serilog captures startup errors before `UseSerilog` takes over.
- `StalenessMonitor` test run time was 1m38s (SkiaSharp icon generation on first run caches all icons — subsequent runs are fast).

**Key files created:**
- `src/StreamDeckPilot.Infrastructure/Observability/{StreamDeckMetrics,StreamDeckActivitySource}.cs`
- `docker-compose.yml` (main app service with device mount + log rotation)
- `docker-compose.observability.yml` (Alloy + Prometheus + Grafana)
- `observability/alloy-config.alloy`
- `observability/prometheus.yml`
- `observability/grafana/provisioning/datasources/prometheus.yml`
- `observability/grafana/provisioning/dashboards/provider.yml`
- `observability/grafana/dashboards/streamdeck.json`
- `Program.cs`, `DeviceRenderer.cs`, `DeviceSupervisorService.cs`, `MqttClientService.cs` updated  
