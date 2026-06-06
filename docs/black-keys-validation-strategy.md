# Black-Keys Validation Strategy

Symptom: keys are black, but `SetKeyBitmap` returns without exception and
`RenderOperations` increments. The pipeline *thinks* it succeeded.

Variables used below:
```bash
API=http://homelab/streamdeck
KEY="X-Api-Key: $API_KEY"
SERIAL=A7FZA5191LB60S
```

---

## 0. Root-cause hypothesis (read the code first)

Confirmed by reading the source:

- `DeviceSupervisorService.HandleConnectionChangeAsync` on connect calls **only**
  `RenderFromDesiredStateAsync` → `_renderer.RenderAll`. **There is no
  `board.SetBrightness(...)` call anywhere in the supervisor, renderer, or
  composer.** The device runs at whatever brightness the firmware last held.
- After a USB reset / container restart / hidraw re-open on Linux, the MK.2
  frequently comes up at **brightness 0**. Bitmaps land correctly but the
  backlight is off ⇒ all keys read as black while every render call "succeeds".

So **failure mode (a) Brightness=0 is the prime suspect.** The sequence below
is ordered to confirm/kill (a) first (cheapest), then differentiate (b)/(c)/(d).

---

## 1. Confirm the device is actually Connected (rules out a non-event)

```bash
curl -s -H "$KEY" $API/devices/$SERIAL/status
curl -s -H "$KEY" $API/devices | jq '.[] | {serial, connectionState}'
```
Expect `"connectionState":"Connected"`. If `Faulted`/`Disconnected`, stop —
this is supervision, not rendering. Look in logs for:

```bash
docker logs streamdeck-pilot 2>&1 | grep -E "disconnected|Re-render failed|Failed to enumerate|Failed to get serial"
```

## 2. Force a render and confirm the pipeline reports success

```bash
curl -s -X POST -H "$KEY" $API/devices/$SERIAL/force-render
docker logs --since 30s streamdeck-pilot 2>&1 | grep -E "connected — rendering|Re-render failed"
```

Then inspect the OTLP metrics counters (or log them — see §6 additions):
- `RenderOperations` increments by ~number of keys ⇒ `SetKeyBitmap` was called
  without throwing.
- `RenderFailures` == 0 ⇒ no exception in compose/encode/USB write.

**Decision:** RenderOperations climbing + keys still black ⇒ the bytes are
leaving the app cleanly. That **points away from (c)/(d) and toward (a) or (b).**

## 3. Discriminator test — drive a known full-white bitmap

Push a high-contrast all-white state and force render. White is the strongest
signal: if the backlight is on at all, white keys are unmistakable; if they
stay black, brightness or the USB write is the cause, not pixel content.

```bash
curl -s -X PUT -H "$KEY" -H "Content-Type: application/json" -d '{
  "schemaVersion":1,"serial":"'"$SERIAL"'",
  "pages":[{"pageType":"ButtonGrid","pageId":"main","buttons":[
    {"buttonId":"diag","keyIndex":0,"pageId":"main",
     "display":{"baseIcon":null,"staticLabel":"WHITE","formatTemplate":"{label}"},
     "rules":[{"condition":">=0","backgroundColour":"#FFFFFF","icon":null}],
     "gestures":{}}]}]}' \
  $API/devices/$SERIAL/config
curl -s -X POST -H "$KEY" $API/devices/$SERIAL/force-render
```

| Observation                                   | Implicates              |
|-----------------------------------------------|-------------------------|
| Key 0 still fully black, RenderOps incremented| (a) brightness or (c) USB |
| Faint grey but no white                       | (a) brightness low, not 0 |
| White appears                                 | rendering OK — bug is content/rules (not in scope) |

## 4. Differentiating (a) vs (c) — does the USB write physically happen?

Both look identical from app logs. Separate them out-of-band:

```bash
# USB traffic on the bus while a force-render runs (run on the host):
sudo usbhid-dump -es                 # or:
sudo cat /sys/kernel/debug/usb/usbmon/<bus>u   # while issuing force-render
```
- Bursts of OUT transfers during force-render ⇒ data **is** reaching USB ⇒
  rules out (c) ⇒ leaves **(a) brightness**.
- No transfers despite RenderOps incrementing ⇒ the SDK is buffering/no-oping
  the write ⇒ **(c)**, investigate `IMacroBoard`/hidapi backend.

## 5. Differentiating (b) wrong pixel format vs (d) ImageSharp produced zeros

Both produce "black on device, no exception". Dump the bytes the composer
emits *before* they hit the SDK (see §6.2 addition) and inspect on the host:

```bash
docker exec streamdeck-pilot ls -l /tmp/diag-key0.*   # written by added code
docker cp streamdeck-pilot:/tmp/diag-key0.png ./diag-key0.png
docker cp streamdeck-pilot:/tmp/diag-key0.jpg ./diag-key0.jpg
```
- `diag-key0.png` (raw ImageSharp before encode) is **all black** ⇒ **(d)**
  ImageSharp drew nothing (font/icon load failed, `Mutate` no-op, wrong region).
- `diag-key0.png` shows correct white, but `diag-key0.jpg` (post SDK JPEG/format
  conversion) is black or channel-swapped ⇒ **(b)** pixel-format / encoder bug
  (e.g. RGB↔BGR, premultiplied alpha, JPEG subsampling on a 72×72 frame).

---

## 6. Code additions (temporary, rebuild + observe)

The current code is silent about per-key bytes. Add these, rebuild the image,
re-run §2–§5. All are removable one-liners.

### 6.1 Log brightness + per-key render at INFO in `DeviceRenderer.RenderButton`

Replace the body of `RenderButton` (Infrastructure/Rendering/DeviceRenderer.cs):

```csharp
public void RenderButton(IMacroBoard board, string serial, int keyIndex, ButtonRenderState state)
{
    if (!board.IsConnected) return;
    try
    {
        var bitmap = _composer is not null ? _composer.Compose(state, serial) : FallbackBitmap(state);
        board.SetKeyBitmap(keyIndex, bitmap);
        _metrics?.RenderOperations.Add(1, [new("serial", serial)]);
        // TEMP DIAG: prove the call path + bitmap identity
        Console.WriteLine($"[DIAG] render serial={serial} key={keyIndex} " +
            $"bg={state.BackgroundColour} dimmed={state.IsDimmed} " +
            $"bitmapW={bitmap.Width} bitmapH={bitmap.Height}");
    }
    catch (Exception ex)
    {
        _metrics?.RenderFailures.Add(1, [new("serial", serial)]);
        Console.WriteLine($"[DIAG] render FAILED key={keyIndex}: {ex}");   // TEMP
        throw;
    }
}
```
Grep: `docker logs streamdeck-pilot 2>&1 | grep '\[DIAG\] render'`
Confirms (vs. metrics) the loop runs for every key and the bitmap is non-zero size.

### 6.2 Dump composed pixels in `KeyBitmapComposer.Compose` (separates b vs d)

Before `return KeyBitmap.Create.FromImageSharpImage(image);`:

```csharp
#if DIAG
// Raw ImageSharp frame BEFORE the SDK touches it.
var nonBlack = 0;
image.ProcessPixelRows(acc => {
    for (int y = 0; y < acc.Height; y++) {
        var row = acc.GetRowSpan(y);
        foreach (ref readonly var px in row)
            if (px.R != 0 || px.G != 0 || px.B != 0) nonBlack++;
    }
});
Console.WriteLine($"[DIAG] compose key bg={state.BackgroundColour} nonBlackPx={nonBlack}/{Size*Size}");
if (state.BackgroundColour == "#FFFFFF")   // only dump the diag key
    image.SaveAsPng($"/tmp/diag-key0.png");
#endif
```
- `nonBlackPx == 0` for a white state ⇒ **(d)** ImageSharp produced zeros.
- `nonBlackPx == 5184` (72×72) but device black ⇒ ImageSharp fine; bug is later
  (SDK format → (b), or brightness → (a)).

### 6.3 THE FIX for (a): call `SetBrightness` on connect

In `DeviceSupervisorService.HandleConnectionChangeAsync`, in the
`if (connected)` branch, **before** `RenderFromDesiredStateAsync`:

```csharp
if (connected)
{
    var wasConnected = _states.TryGetValue(serial, out var prev) && prev == DeviceConnectionState.Connected;
    _states[serial] = DeviceConnectionState.Connected;
    if (wasConnected) _metrics?.DeviceReconnects.Add(1, [new("serial", serial)]);

    try
    {
        board.SetBrightness(80);   // 0–100; 80 = bright, comfortable default
        _logger.LogInformation("Device {Serial} brightness set to {Pct}%", serial, 80);
    }
    catch (Exception ex) { _logger.LogError(ex, "SetBrightness failed for {Serial}", serial); }

    _logger.LogInformation("Device {Serial} connected — rendering from desired state", serial);
    try { await RenderFromDesiredStateAsync(serial, board); }
    catch (Exception ex) { _logger.LogError(ex, "Re-render failed for {Serial}", serial); }
}
```

---

## Answers to the brightness question

- **What change:** add `board.SetBrightness(<pct>)` — it is currently never
  called, so the device keeps firmware/previous brightness, commonly 0 after a
  USB reset in Docker.
- **Where:** `DeviceSupervisorService.HandleConnectionChangeAsync`, inside the
  `if (connected)` block, **before** `RenderFromDesiredStateAsync`. This makes
  it part of the desired-state projection: it re-applies on every connect *and*
  every reconnect, matching the "re-render from desired state on reconnect"
  contract. Do **not** put it only in one-time discovery (`ScanAsync`), or a
  reconnect after a sleep/replug would come back dark.
- **What value:** **80** (range is **0–100**). 80 is bright and readable without
  full-power heat/burn-in. Make it an env var (`STREAMDECK_BRIGHTNESS`, default
  80) so it is tunable without rebuild. 100 if maximum visibility is wanted.
- **When in lifecycle:** on the `Connected` transition, before the first render,
  every time — connect and reconnect alike. Brightness is part of desired state,
  not a boot-once action.

## Quick triage flow

1. status Connected? no → supervision bug, stop.
2. force-render → RenderOps++ and RenderFailures 0? no → (c)/(d), go §6.1/6.2.
3. push WHITE (§3): white shows → content bug (out of scope).
4. still black + RenderOps++ → almost certainly **(a)**: apply §6.3, redeploy,
   force-render. Keys light up ⇒ confirmed.
5. if §6.3 does NOT fix it: usbmon (§4) for (c); pixel dump (§6.2) for (b)/(d).
