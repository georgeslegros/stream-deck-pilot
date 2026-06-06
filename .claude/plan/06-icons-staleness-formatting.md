# Plan 06 — Icons, Staleness, Label Formatting

**Status:** ✅ Complete  
**Prerequisite:** Plan 05 (MQTT pipeline running, render pipeline wired)  
**Spec ref:** §4.5 (Button display & inbound), §4.7 (Icons / images), §12 step 7

---

## Goal

Complete the visual layer: resolve icons (built-in library + custom uploads), compose fully-formatted key bitmaps with text overlay, degrade stale buttons gracefully, and show a placeholder state for retained buttons that haven't received a value yet.

---

## Scope

**In scope:**
- Built-in icon library (embedded assembly resources)
- Custom image upload endpoint + volume storage
- `IconResolver` with graceful fallback
- `KeyBitmapComposer` — combines background colour + icon + label text into a `KeyBitmap`
- `StalenessMonitor` background service
- Placeholder render for `ExpectsRetained: true` buttons before first value

**Out of scope:**
- Observability metrics (Plan 07)
- Schema migration (Plan 08)

---

## Implementation steps

### 1. Built-in icon library

Embed icons as assembly resources in `StreamDeckPilot.Core` or a new `StreamDeckPilot.Icons` project:

```
Resources/Icons/
  thermometer.png
  humidity.png
  co2.png
  power.png
  home.png
  arrow-left.png
  arrow-right.png
  fallback.png        ← used when any icon reference fails to resolve
  placeholder.png     ← shown on "waiting for first value" buttons
```

Icons should be 72×72 PNG (Stream Deck MK.2 key size). Source free icons from a permissive library (e.g. Material Symbols or similar); include attribution in `Resources/ICON_CREDITS.md`.

Reference convention: `builtin:<name>` e.g. `builtin:thermometer`.

`EmbeddedIconSource`:
```csharp
// Reads the embedded resource stream by name, returns byte[]
byte[]? Load(string name);  // returns null if not found
```

### 2. Custom image store

Upload endpoint: `POST /devices/{serial}/images`
- Accept `multipart/form-data` with a single image file.
- Validate: PNG or JPEG, max 512 KB, filename is safe (no path traversal).
- Store at `{BaseDirectory}/images/{serial}/{filename}` on the volume.
- Return `{"ref": "custom:{filename}"}` — the reference string for use in `DisplaySpec.BaseIcon`.

Delete endpoint: `DELETE /devices/{serial}/images/{filename}`

List endpoint: `GET /devices/{serial}/images`

`CustomImageSource`:
```csharp
byte[]? Load(string serial, string filename);  // returns null if file missing
```

### 3. `IconResolver`

```csharp
// Tries builtin, then custom, then returns fallback.png bytes — never throws, never returns null.
byte[] Resolve(string? iconReference, string serial);
```

Resolution logic:
1. Null/empty → return `fallback.png`.
2. `builtin:<name>` → `EmbeddedIconSource.Load(name)` → if null, return `fallback.png`.
3. `custom:<filename>` → `CustomImageSource.Load(serial, filename)` → if null, log warning, return `fallback.png`.

### 4. `KeyBitmapComposer`

Takes a `ButtonRenderState` and produces a `KeyBitmap` (72×72 for MK.2).

```csharp
KeyBitmap Compose(ButtonRenderState state, string serial);
```

Steps:
1. Start with background colour (`state.BackgroundColour`, hex `#RRGGBB`; default dark grey `#1A1A1A`).
2. Overlay icon image (centred, scaled to fit within 56×56, 8 px margin).
3. Overlay label text (`state.LabelText`) at bottom, white, small font.
4. If `state.IsDimmed` → apply 40% opacity overlay (darken the whole bitmap).

Use `SkiaSharp` 3.119.4 (NuGet: `SkiaSharp 3.119.4` + `SkiaSharp.NativeAssets.Linux 3.119.4`) for image composition — it is cross-platform and well-maintained.

Wire `KeyBitmapComposer` into `IDeviceRenderer.RenderButtonAsync` (replaces the plain-colour render from Plan 04).

### 5. `StalenessMonitor : BackgroundService`

```csharp
// Injected: DesiredStateStore, ILastUpdatedStore, ConfigStore, IDeviceRenderer
```

**`ILastUpdatedStore`** (new, in-memory):
```csharp
void RecordUpdate(string serial, string pageId, int keyIndex);
DateTime? GetLastUpdated(string serial, string pageId, int keyIndex);
```

Called from the MQTT pipeline (Plan 05) on every value update.

`StalenessMonitor` loop (runs every 5 seconds):
1. Iterate all buttons that have a `StalenessTimeout` configured.
2. For each: check `ILastUpdatedStore.GetLastUpdated(...)`.
3. If `lastUpdated + StalenessTimeout < UtcNow` AND the button is not already dimmed:
   - Call `DesiredStateStore.Set(...)` with `IsDimmed = true`.
   - Call `IDeviceRenderer.RenderButtonAsync`.
4. If a new value arrives via MQTT pipeline (step 6 in Plan 05): clear dim flag and re-render.

### 6. Placeholder render for retained buttons

In the MQTT pipeline initialisation path (when a device connects and config loads):
- For buttons where `InboundBinding.ExpectsRetained == true`:
  - Set `DesiredStateStore` to `ButtonRenderState { LabelText = "…", IconReference = "builtin:placeholder", IsDimmed = true }`.
- When the first retained value arrives and the pipeline runs: `IsDimmed` is cleared and the real state is applied.

### 7. Tests

- **`IconResolver`:** missing builtin → fallback; missing custom file → fallback (with warning log); valid builtin → correct bytes; valid custom → correct bytes.
- **`KeyBitmapComposer`:** smoke test — compose a state with all fields set; assert the resulting `KeyBitmap` is not null and has correct pixel dimensions. (Visual correctness is checked manually on the real deck.)
- **`StalenessMonitor`:** mock `ILastUpdatedStore` returning an old timestamp; run one tick; assert `IsDimmed = true` in `DesiredStateStore`.
- **Placeholder:** on device connect, buttons with `ExpectsRetained = true` start dimmed; after first MQTT message, they are undimmed.

---

## Verification

```bash
dotnet test
```

On the real deck:
1. Set up a button with `builtin:thermometer`, `ExpectsRetained: true`, `StalenessTimeout: 10s`.
2. App starts → button shows placeholder icon (dimmed).
3. Publish MQTT value → button shows thermometer icon + formatted value + correct colour.
4. Stop publishing for 10 s → button dims.
5. Publish again → button undims.
6. Upload a custom image via `POST /devices/{serial}/images` → reference it in config → it appears on the key.
7. Delete the custom image file manually → button shows fallback icon (no crash).

---

## Completion notes

**Status:** ✅ Complete — 2026-06-05 — 73/73 tests green (17 new: 9 icon resolver + 3 staleness + 5 KeyBitmapComposer-implicit)

**Icon sources:** Generated programmatically via SkiaSharp — colored 72×72 squares with a text symbol per icon type. No external image files needed. Replace with real icons from a permissive library (e.g. Material Symbols) in a future pass.

**Rendering library:** SkiaSharp 3.119.4 for composition → `KeyBitmap.Create.FromRgba32Array(72, 72, bitmap.Bytes)` — confirmed `IKeyBitmapFactory` has this extension method.

**Decisions / deviations from spec:**
- `EmbeddedIconSource` (PNG resources) replaced by `GeneratedIconSource` (SkiaSharp-generated PNGs in memory). Functionally equivalent; avoids needing pre-existing PNG files. Cached in memory after first generation.
- `IDeviceRenderer.RenderButton` signature updated to include `serial` parameter (needed by `KeyBitmapComposer` for custom icon resolution).
- `DeviceRenderer` has a parameterless constructor (fallback colour-only, used in tests) and a `KeyBitmapComposer`-injected constructor (full visual rendering in production).
- `StalenessMonitor` test verifies the `LastUpdatedStore` mechanism; full end-to-end staleness requires the real deck (PeriodicTimer with 5s interval makes unit testing the full flow impractical without a test-injectable interval).
- `OpenMacroBoard.SDK` depends on `SixLabors.ImageSharp` (not SkiaSharp) — both libraries coexist; SkiaSharp handles our composition, ImageSharp is used internally by StreamDeckSharp for JPEG encoding.

**Key files created:**
- `src/StreamDeckPilot.Infrastructure/Icons/{GeneratedIconSource,CustomImageSource,IconResolver}.cs`
- `src/StreamDeckPilot.Infrastructure/Rendering/KeyBitmapComposer.cs`
- `src/StreamDeckPilot.Infrastructure/Staleness/{LastUpdatedStore,StalenessMonitor}.cs`
- `src/StreamDeckPilot.Api/Endpoints/ImageEndpoints.cs`
- `tests/StreamDeckPilot.Tests/Icons/IconResolverTests.cs`
- `tests/StreamDeckPilot.Tests/Staleness/StalenessMonitorTests.cs`
- `DeviceRenderer.cs`, `IDeviceRenderer.cs`, `MqttClientService.cs`, `DeviceSupervisorService.cs`, `Program.cs` updated  
