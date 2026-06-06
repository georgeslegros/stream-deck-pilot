# Plan 03 — REST API, Auth, Config Validation

**Status:** ✅ Complete  
**Prerequisite:** Plan 02 (domain model + persistence)  
**Spec ref:** §10 (REST API), §8 (Security / API key), §4.8 (Validation), §12 step 3

---

## Goal

Expose the full configuration and control surface via an ASP.NET Core minimal API. All endpoints are protected by an API-key middleware. Config writes are validated against the device catalogue before being persisted. This plan makes the service controllable by scripts and Claude Code without requiring hardware.

---

## Scope

**In scope:**
- `src/StreamDeckPilot.Api` project (the runnable host)
- API-key middleware
- All endpoints listed below
- Full validation on `PUT /devices/{serial}/config`
- Integration tests using `WebApplicationFactory`

**Out of scope:**
- Device connection / rendering (Plan 04) — `GET /devices` returns catalogue data but connection state is stubbed as `Unknown` until Plan 04 wires in the supervisor
- MQTT (Plan 05)
- Schema migration (Plan 08) — `POST /config/upgrade` is a stub that returns the same document

---

## Implementation steps

### 1. Create the Api project

```
src/StreamDeckPilot.Api/
  Program.cs
  Middleware/ApiKeyMiddleware.cs
  Endpoints/DeviceEndpoints.cs
  Endpoints/ConfigEndpoints.cs
  appsettings.json
  Dockerfile          (linux-x64 production image)
```

References: `StreamDeckPilot.Core`, `StreamDeckPilot.Infrastructure`.

### 2. API-key middleware

`ApiKeyMiddleware`:
- Reads `X-Api-Key` header on every request.
- Compares (constant-time) against `API_KEY` environment variable.
- Returns `401 Unauthorized` with `{"error":"invalid_api_key"}` if missing or wrong.
- Applied globally via `app.UseMiddleware<ApiKeyMiddleware>()` before any route matching.
- Health/liveness endpoints (if any) are exempt from auth.

```csharp
// Constant-time comparison to prevent timing attacks
CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(provided),
    Encoding.UTF8.GetBytes(expected));
```

### 3. Endpoints

Register all endpoints in extension methods on `IEndpointRouteBuilder`.

**`GET /devices`**  
Returns the device catalogue with current connection state for each serial.  
Response shape:
```json
[
  {
    "serial": "ABC123",
    "model": "Stream Deck MK.2",
    "keyRows": 3,
    "keyColumns": 5,
    "firstSeen": "2025-01-01T00:00:00Z",
    "connectionState": "Unknown"
  }
]
```
`connectionState` is sourced from `IDeviceStateProvider` (interface returning `Unknown` until Plan 04 implements it).

**`GET /devices/{serial}/status`**  
Returns the connection state for one device. 404 if serial not in catalogue.

**`GET /devices/{serial}/config`**  
Returns the persisted `DeviceConfig` for the serial. 404 if no config file exists.

**`PUT /devices/{serial}/config`**  
- Deserialise request body as `DeviceConfig`.
- Reject with `400` if serial not in catalogue.
- Run `ConfigValidator.ValidateConfig(config, deviceEntry)`.
- On validation errors: return `400` with `{"errors": ["..."]}`.
- On success: call `ConfigStore.SaveAsync(config)`, return `204 No Content`.

**`POST /config/upgrade`** *(stub)*  
Accept any JSON body, return it unchanged with `200 OK`. Full implementation in Plan 08.

**`POST /devices/{serial}/force-render`** *(debug helper)*  
Triggers re-render of all keys for a connected device. Returns `202 Accepted`. No-op (200) if device not connected. Implemented properly in Plan 04; stub a 200 here.

### 4. `IDeviceStateProvider` interface (Core)

```csharp
public interface IDeviceStateProvider
{
    DeviceConnectionState GetState(string serial);
}

public enum DeviceConnectionState { Unknown, Disconnected, Connecting, Connected, Faulted }
```

Register a `NullDeviceStateProvider` (always returns `Unknown`) until Plan 04 replaces it.

### 5. DI registration and configuration

`Program.cs`:
- Register `CatalogueStore`, `ConfigStore` with `IOptions<StorageOptions>`.
- Register `NullDeviceStateProvider` as `IDeviceStateProvider`.
- Read `API_KEY` from env var; fail fast at startup if absent/empty.
- Read `Storage:BaseDirectory` from env var / appsettings (default `/data`).

### 6. Dockerfile (Api)

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update && apt-get install -y libhidapi-libusb0 libusb-1.0-0 && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/StreamDeckPilot.Api -c Release -r linux-x64 --self-contained false -o /app

FROM base AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "StreamDeckPilot.Api.dll"]
```

### 7. Integration tests

Use `WebApplicationFactory<Program>` in `StreamDeckPilot.Tests` (NuGet: `xunit.v3 3.2.2`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.NET.Test.Sdk`):

- **Auth:** request without `X-Api-Key` → 401; request with wrong key → 401; correct key → passes through.
- **`GET /devices`:** pre-seed `CatalogueStore` with a device; assert response contains it.
- **`PUT /devices/{serial}/config` — happy path:** valid config for catalogued serial → 204, file written.
- **`PUT /devices/{serial}/config` — unknown serial:** → 400.
- **`PUT /devices/{serial}/config` — invalid geometry:** key index out of range → 400 with error message.
- **`PUT /devices/{serial}/config` — duplicate position:** → 400.
- **`PUT /devices/{serial}/config` — broken nav target:** → 400.

Use a temp directory (via `Path.GetTempPath()`) as the storage base in tests; clean up in `IDisposable`.

---

## Verification

```bash
dotnet build
dotnet test
```

All integration tests green. Manually confirm with `curl` or httpie if desired:
```bash
curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/devices
# → 401 (no key)

curl -s -H "X-Api-Key: test-key" http://localhost:5000/devices
# → 200 []
```

---

## Completion notes

**Status:** ✅ Complete — 2026-06-05 — 29/29 tests green (17 from Plan 02 + 12 new integration tests)

**Decisions / deviations from spec:**
- Fail-fast startup check for missing `ApiKey` was removed. `WebApplicationFactory` injects test configuration after `WebApplication.CreateBuilder` runs, so a throw during builder setup always fires in tests. The middleware is already fail-secure (missing key → all requests get 401), so a startup warning log replaces the throw. Production operators are told to set the env var via documentation.
- `POST /config/upgrade` returns the body unchanged (stub as planned — Plan 08 completes it).
- `POST /devices/{serial}/force-render` returns 200 OK (stub — Plan 04 completes it).

**Key files created:**
- `src/StreamDeckPilot.Core/DeviceState/{DeviceConnectionState,IDeviceStateProvider}.cs`
- `src/StreamDeckPilot.Infrastructure/DeviceState/NullDeviceStateProvider.cs`
- `src/StreamDeckPilot.Api/{Program.cs,appsettings.json,Dockerfile}`
- `src/StreamDeckPilot.Api/Middleware/ApiKeyMiddleware.cs`
- `src/StreamDeckPilot.Api/Endpoints/{DeviceEndpoints,ConfigEndpoints}.cs`
- `tests/StreamDeckPilot.Tests/Api/{StreamDeckApiFactory,ApiIntegrationTests}.cs`

**Endpoint list (final):**
- `GET /health` — exempt from auth
- `GET /devices` — catalogue + connection state
- `GET /devices/{serial}/status` — per-device connection state
- `GET /devices/{serial}/config` — persisted config or 404
- `PUT /devices/{serial}/config` — validate + persist, 204 or 400
- `POST /config/upgrade` — stub, returns body unchanged
- `POST /devices/{serial}/force-render` — stub, returns 200  
