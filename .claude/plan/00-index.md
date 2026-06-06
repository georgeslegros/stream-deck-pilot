# Stream Deck Pilot — Implementation Plan Index

Each plan is a self-contained unit of work. Execute them in order; later plans build on earlier ones. Update each plan's status badge and completion notes when done.

## Plans

| # | File | Title | Status | Depends on |
|---|------|-------|--------|------------|
| 01 | [01-device-spike.md](01-device-spike.md) | Hardware Validation — USB + Docker | ✅ Complete | — |
| 02 | [02-scaffold-domain-persistence.md](02-scaffold-domain-persistence.md) | Project Scaffold, Domain Model, Persistence | ✅ Complete | 01 |
| 03 | [03-rest-api-auth-validation.md](03-rest-api-auth-validation.md) | REST API, Auth, Config Validation | ✅ Complete | 02 |
| 04 | [04-device-supervision-runtime.md](04-device-supervision-runtime.md) | Device Supervision + Desired-State Runtime | ✅ Complete | 02 |
| 05 | [05-mqtt-pipeline-actions.md](05-mqtt-pipeline-actions.md) | MQTT Inbound Pipeline + Outbound Actions | ✅ Complete | 03, 04 |
| 06 | [06-icons-staleness-formatting.md](06-icons-staleness-formatting.md) | Icons, Staleness, Label Formatting | ✅ Complete | 05 |
| 07 | [07-observability.md](07-observability.md) | OpenTelemetry, Metrics, Structured Logs | ✅ Complete | 05 |
| 08 | [08-schema-migration.md](08-schema-migration.md) | Schema Versioning + Migration + Upgrade Endpoint | ✅ Complete | 03 |

## Dependency graph

```
01 (spike)
 └─ 02 (domain + persistence)
     ├─ 03 (REST API) ──────────────────────── 08 (migration)
     └─ 04 (device supervision)
         └─ 05 (MQTT pipeline + actions)
             ├─ 06 (icons, staleness, formatting)
             └─ 07 (observability)
```

## Pinned versions (verified 2026-06-05)

| Technology | Version |
|-----------|---------|
| .NET / Docker base images | **10.0 LTS** (`mcr.microsoft.com/dotnet/{sdk,aspnet,runtime}:10.0`) |
| StreamDeckSharp | 6.1.0 |
| OpenMacroBoard.SDK | 6.1.0 |
| MQTTnet | 5.1.0.1559 |
| SkiaSharp + SkiaSharp.NativeAssets.Linux | 3.119.4 |
| Serilog.AspNetCore | 10.0.0 |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.15.3 |
| OpenTelemetry.Extensions.Hosting | 1.15.3 |
| OpenTelemetry.Instrumentation.AspNetCore | 1.15.2 |
| OpenTelemetry.Instrumentation.Runtime | 1.15.1 |
| Testcontainers.RabbitMq | 4.12.0 |
| xunit (test framework) | **xunit.v3 3.2.2** (`xunit` v2 is deprecated; use `xunit.v3` package) |

## Spec reference

All plans reference `streamdeck-service-spec.md` (the authoritative brief). Section numbers in plan files refer to that document (e.g. §4.5, §7, §12).
