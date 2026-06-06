# Plan 01 — Hardware Validation: Local Spike → Linux/Docker

**Status:** ✅ Complete  
**Prerequisite:** none — this is the first thing to execute  
**Spec ref:** §3 (Target Hardware), §2 (Native dependencies), §12 step 1

---

## Goal

Prove that the chosen library can communicate with the specific Stream Deck MK.2 Scissor Switch unit before any application code is written. This is done in two sequential steps:

1. **Step A — Windows laptop:** run a plain console app on the dev machine with the device plugged in directly. This is the fastest way to catch product-ID issues with zero infrastructure.
2. **Step B — Linux/Docker on homelab:** once the device is known-good, wrap the same binary in a Linux container and validate the USB path, `libhidapi`, and device-mount setup on the target server.

If Step A reveals the device is unrecognised, resolve that (upstream PR or fork) before attempting Step B.

---

## Scope

**In scope:**
- Minimal C# console spike in `/spike/StreamDeckSpike/`
- Windows run (Step A) and Linux Docker run (Step B)
- Product-ID triage and fork workflow if needed

**Out of scope:**
- Any domain model, API, or MQTT code
- The spike can be deleted after Plan 02; it does not need tests or cleanup

---

## Step A — Windows laptop test

### 1. Create the spike project

```
spike/
  StreamDeckSpike/
    StreamDeckSpike.csproj
    Program.cs
```

`StreamDeckSpike.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="StreamDeckSharp" Version="6.1.0" />
    <PackageReference Include="OpenMacroBoard.SDK" Version="6.1.0" />
  </ItemGroup>
</Project>
```

`Program.cs`:
```csharp
using StreamDeckSharp;
using OpenMacroBoard.SDK;

var devices = StreamDeck.EnumerateDevices().ToList();
Console.WriteLine($"Found {devices.Count} device(s).");
if (devices.Count == 0) { Console.Error.WriteLine("No devices found."); return 1; }

using var deck = StreamDeck.OpenDevice(devices[0]);
Console.WriteLine($"Opened: {devices[0].DeviceName} (keys: {deck.Keys.Count})");

deck.SetBrightness(80);
var green = KeyBitmap.Create.FromRgb(0, 200, 0);
deck.SetKeyBitmap(0, green);

Console.WriteLine("Key 0 should be green. Waiting 5 s…");
await Task.Delay(5000);

deck.ClearKeys();
Console.WriteLine("Done.");
return 0;
```

### 2. Run on Windows

Plug in the Stream Deck, then:
```powershell
cd spike/StreamDeckSpike
dotnet run
```

**Expected:** key 0 lights green for 5 s, console exits 0.

### 3. If the device is NOT found (product-ID issue)

The MK.2 Scissor Switch may have a USB product ID that `StreamDeckSharp 6.1.0` does not recognise. To diagnose on Windows:

- Open **Device Manager → Universal Serial Bus devices**, find the Stream Deck entry, and note the `PID` (e.g. `0x009a`).  
  Or use [UsbTreeView](https://www.uwe-sieber.de/usbtreeview_e.html) / PowerShell:
  ```powershell
  Get-PnpDevice -Class USB | Where-Object { $_.FriendlyName -like "*Stream Deck*" }
  ```
- Note the product ID and the product string (e.g. "Stream Deck MK.2").

**Triage path:**
1. Check the [StreamDeckSharp GitHub issues](https://github.com/OpenMacroBoard/StreamDeckSharp/issues) for an existing report/PR for this PID.
2. **If a fix exists upstream:** use the pre-release NuGet or reference the fixed commit via a local `Directory.Build.props` source link, re-run.
3. **If no fix exists → fork the library:**

   ```
   Fork: https://github.com/OpenMacroBoard/StreamDeckSharp
   ```

   In the fork, find the device definition file (likely `StreamDeckSharp/HidDeviceIds.cs` or similar) and add the new PID alongside the existing MK.2 entry. Then:
   - Reference the fork in `StreamDeckSpike.csproj` via a local path or a GitHub package source.
   - Submit a PR to upstream with the new PID + your USB descriptor output as evidence.
   - The fork reference stays in the project until upstream merges and publishes; then switch back to the official NuGet.

   The same fork reference will carry forward into Plan 02 (the main project) if needed.

---

## Step B — Linux/Docker on homelab

Only proceed once Step A exits 0 on Windows.

### 1. Write the Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
RUN apt-get update \
    && apt-get install -y libhidapi-libusb0 libusb-1.0-0 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY StreamDeckSpike/ .
RUN dotnet publish -c Release -r linux-x64 --self-contained false -o /app

FROM base AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "StreamDeckSpike.dll"]
```

> If the homelab server is ARM64 (check with `uname -m`), change the publish RID to `linux-arm64`.

### 2. Add the udev rule on the host (do this before the first docker run)

```bash
echo 'SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0fd9", MODE="0660", GROUP="docker"' \
  | sudo tee /etc/udev/rules.d/50-streamdeck.rules
sudo udevadm control --reload-rules && sudo udevadm trigger
```

### 3. Identify the hidraw node

```bash
# Before plugging in:
ls /dev/hidraw*
# Plug in the Stream Deck, then:
ls /dev/hidraw*
# The new entry is the device (e.g. /dev/hidraw2)
```

### 4. Build and run

```bash
# Copy spike/ to the homelab server, then:
docker build -t sdspike ./spike

# Preferred — tighter device cgroup rule:
docker run --rm --device /dev/hidraw2 sdspike

# Fallback if device index is wrong or udev rule not yet active:
docker run --rm --privileged sdspike
```

### 5. If the device is not found under Docker (but Step A passed)

This is a HID access issue, not a product-ID issue. Checklist:
- Confirm the correct hidraw node is mounted (`--device /dev/hidrawX`).
- Confirm the udev rule has been applied and the container is in the `docker` group.
- Try `--privileged` as a diagnostic (if that works, it's a permissions issue).
- Check that the host kernel version supports the `hidraw` subsystem (`ls /sys/class/hidraw`).

---

## Verification

**Step A:** `dotnet run` exits 0; key 0 glows green on the physical deck.  
**Step B:** `docker run` exits 0; same visual result on the physical deck.

---

## Completion notes

**Step A — completed 2026-06-05**

**Status:** 🔶 Step A ✅ — Step B ⬜ pending  
**Step A result:** PID `0x00A5` (Stream Deck MK.2 Scissor Switch) is NOT registered in StreamDeckSharp 6.1.0 (only `0x0080` is). Device showed up in Windows PnP as `HID\VID_0FD9&PID_00A5`.  
**Fork status:** Not needed. `Hardware.RegisterNewHardware()` is a public API designed for exactly this case. Calling it at startup with `new UsbVendorProductPair(0x0FD9, 0x00A5)`, same JPEG driver and 5×3 layout as MK.2, resolves enumeration. Library exits 0, 15 keys confirmed, key 0 lit green.

**⚠️ Carry-forward to all future plans:** Every entry point that calls `StreamDeck.EnumerateDevices()` or `StreamDeck.OpenDevice()` must first call:
```csharp
Hardware.RegisterNewHardware(
    usbId: new UsbVendorProductPair(0x0FD9, 0x00A5),
    deviceName: "Stream Deck MK.2 (Scissor Switch)",
    keyLayout: new GridKeyLayout(5, 3, 72, 32),
    driver: new HidComDriverStreamDeckJpeg(72) { BytesPerSecondLimit = 1_500_000 }
);
```
In the main application this belongs in `DeviceSupervisorService` startup (Plan 04), or in a dedicated `StreamDeckHardwareRegistration.Register()` call in `Program.cs`.

**Step B — completed 2026-06-05**  
Key 0 lit green inside Docker on the homelab server. USB/HID path, libhidapi, and device mount all confirmed working end-to-end.  
