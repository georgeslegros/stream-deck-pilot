# Plan 04 — Device Supervision + Desired-State Runtime

**Status:** ✅ Complete  
**Prerequisite:** Plan 02 (domain model + persistence)  
**Spec ref:** §7 (Runtime & Resilience), §4.2 (Device states), §12 step 4

---

## Goal

Implement the device connection lifecycle and the desired-state/device-state separation that makes disconnects a non-event. The app always knows what every button _should_ show; physical rendering is a best-effort projection that is replayed from that state on reconnect. Tests use `OpenMacroBoard.SDK`'s virtual board so no hardware is required.

---

## Scope

**In scope:**
- `DeviceSupervisorService` background service (per-device state machine)
- `DesiredStateStore` (in-memory, per-button desired render state)
- Catalogue population on device discovery
- Re-render from desired state on reconnect
- `IDeviceStateProvider` implementation replacing the Plan 03 stub
- Integration with `ConfigStore` (load config on device connect)

**Out of scope:**
- MQTT / inbound value updates (Plan 05)
- Icon resolution / full render pipeline (Plan 06)
- Observability metrics (Plan 07) — add pre-wired no-op counters where needed

---

## Implementation steps

### 1. `DesiredStateStore` (Core or Infrastructure)

Thread-safe in-memory store keyed by `(serial, pageIndex, keyIndex)`.

```csharp
record ButtonRenderState(
    string ButtonId,
    string? BackgroundColour,   // hex "#RRGGBB" or null
    string? IconReference,       // "builtin:x" or "custom:x" or null
    string? LabelText,
    bool IsDimmed);
```

Methods:
- `Set(string serial, int pageIndex, int keyIndex, ButtonRenderState state)`
- `Get(string serial, int pageIndex, int keyIndex) → ButtonRenderState?`
- `GetPage(string serial, int pageIndex) → IReadOnlyList<(int keyIndex, ButtonRenderState)>`
- `Clear(string serial)` — called when a device config is deleted

### 2. Per-device state machine

```
Disconnected → Connecting → Connected → Faulted
      ↑____________________________________________↓ (retry loop)
```

States are expressed as `DeviceConnectionState` enum (defined in Plan 03).  
Transitions:
- On library `ConnectionStateChanged` event → update state
- `Connected` → load config from `ConfigStore` → initialise `DesiredStateStore` with blank state for all buttons → call `RenderAll`
- `Faulted` / `Disconnected` → log warning, wait for reconnect event (do NOT poll; rely on library events)

### 3. `DeviceSupervisorService : BackgroundService`

```csharp
// Injected: IStreamDeckLibraryWrapper, CatalogueStore, ConfigStore,
//            DesiredStateStore, IDeviceStateProvider (its own implementation)
```

On startup:
1. Call `StreamDeckHardwareRegistration.Register()` — a static helper that calls `Hardware.RegisterNewHardware(new UsbVendorProductPair(0x0FD9, 0x00A5), "Stream Deck MK.2 (Scissor Switch)", new GridKeyLayout(5, 3, 72, 32), new HidComDriverStreamDeckJpeg(72) { BytesPerSecondLimit = 1_500_000 })`. PID `0x00A5` is the Scissor Switch revision and is missing from StreamDeckSharp 6.1.0. This must run before any enumeration. (Finding from Plan 01.)
2. Call `IStreamDeckLibraryWrapper.EnumerateDevices()`.
2. For each device found: upsert into `CatalogueStore`, start a supervised loop.
3. Listen for hot-plug events (if the library supports them); on new device: upsert + start loop.

Per-device supervised loop:
- Try to open the device.
- On success: transition to `Connected`, load config, render all buttons.
- Subscribe to `ConnectionStateChanged`; on disconnect: transition to `Disconnected`, no-op all further renders.
- On reconnect event: transition back to `Connected`, re-render from desired state.
- Never propagate exceptions up the loop; catch all, log, back off, retry.

### 4. `IStreamDeckLibraryWrapper` (abstraction over StreamDeckSharp)

Thin interface so tests can substitute a virtual board:
```csharp
public interface IStreamDeckLibrary
{
    IReadOnlyList<IStreamDeckInfo> Enumerate();
    IMacroBoard Open(IStreamDeckInfo info);
}
```

Real implementation wraps `StreamDeck.OpenDevice()`.  
Test implementation returns `VirtualMacroBoard` from `OpenMacroBoard.SDK`.

### 5. `IDeviceRenderer` (Infrastructure)

Converts a `ButtonRenderState` into a `KeyBitmap` and calls `IMacroBoard.SetKeyBitmap`.

```csharp
public interface IDeviceRenderer
{
    Task RenderButtonAsync(IMacroBoard board, int keyIndex, ButtonRenderState state);
    Task RenderAllAsync(IMacroBoard board, string serial, int pageIndex);
}
```

At this stage: render background colour only (icon/label comes in Plan 06).  
`RenderAll`: iterate `DesiredStateStore.GetPage(serial, activePageIndex)` and call `RenderButtonAsync` for each.

Writes to an absent/null board → no-op (do not throw).

### 6. Replace Plan 03 stub

`DeviceSupervisorService` implements `IDeviceStateProvider`.  
Remove `NullDeviceStateProvider`; register the supervisor as both `BackgroundService` and `IDeviceStateProvider` in DI.

Update `GET /devices` and `GET /devices/{serial}/status` to return real states.

### 7. `POST /devices/{serial}/force-render` (complete the Plan 03 stub)

Look up the active board in supervisor; call `IDeviceRenderer.RenderAllAsync`.

### 8. Tests

- **State machine unit test:** mock library wrapper; simulate connect → verify `Connected` state; simulate disconnect event → verify `Disconnected`; simulate reconnect → verify `RenderAllAsync` called.
- **DesiredStateStore:** concurrent Set/Get from multiple threads — no data races.
- **Re-render on reconnect:** set desired state for 5 keys, simulate reconnect, assert all 5 render calls made.
- **Multi-device independence:** two virtual boards; fault one, assert the other is unaffected.

---

## Verification

Run with two virtual boards (test mode):
```bash
STREAM_DECK_VIRTUAL=true dotnet run --project src/StreamDeckPilot.Api
```

Then:
```bash
# Connect the real MK.2, unplug it, plug it back in
# Observe logs: "Device ABC123: Connected", "Device ABC123: Disconnected", "Device ABC123: Re-rendering 15 keys"
dotnet test
```

---

## Completion notes

**Status:** ✅ Complete — 2026-06-05 — 38/38 tests green (9 new supervision tests)

**Library hot-plug support:** StreamDeckSharp 6.1.0 has no hot-plug event/listener API (removed from v6). Already-opened `IMacroBoard` instances fire `ConnectionStateChanged(bool)` on disconnect/reconnect — no re-open needed. For discovering brand-new devices plugged in after startup, `DeviceSupervisorService` polls `EnumerateDevices()` every 10 seconds (configurable, set to 60 min in tests).

**Virtual board approach:** `OpenMacroBoard.SDK.VirtualMacroBoard` does not exist in v6.1.0. Replaced with a hand-written `FakeMacroBoard` test double that records `SetKeyBitmap` calls and exposes `SimulateDisconnect()`/`SimulateReconnect()` helpers. Cleaner than a virtual board anyway — tests are explicit and deterministic.

**Decisions / deviations from spec:**
- Namespace `StreamDeckPilot.Infrastructure.StreamDeck` clashes with `StreamDeckSharp.StreamDeck` — resolved with `using SdSharp = StreamDeckSharp.StreamDeck` alias.
- `IDeviceRenderer` lives in Infrastructure (not Core) since it references `IMacroBoard` from `OpenMacroBoard.SDK`; Core has no external deps.
- `DeviceSupervisorService` exposes `GetBoard()`, `GetActivePage()`, `SetActivePage()` directly (no separate interface) — sufficient for the force-render endpoint and Plan 05 press handling.
- `NullDeviceStateProvider` replaced in Program.cs; supervisor registered as both `IDeviceStateProvider` singleton and `IHostedService`.

**Key files created:**
- `src/StreamDeckPilot.Core/Rendering/ButtonRenderState.cs`
- `src/StreamDeckPilot.Infrastructure/Rendering/{DesiredStateStore,ActivePageStore,IDeviceRenderer,DeviceRenderer}.cs`
- `src/StreamDeckPilot.Infrastructure/StreamDeck/{IStreamDeckLibrary,StreamDeckLibrary,StreamDeckHardwareRegistration}.cs`
- `src/StreamDeckPilot.Infrastructure/Supervision/DeviceSupervisorService.cs`
- `tests/StreamDeckPilot.Tests/Supervision/{FakeMacroBoard,FakeStreamDeckLibrary,DesiredStateStoreTests,DeviceSupervisorTests}.cs`  
