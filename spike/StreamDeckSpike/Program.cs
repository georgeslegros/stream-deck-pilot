using StreamDeckSharp;
using StreamDeckSharp.Internals;
using OpenMacroBoard.SDK;

// PID 0x00A5 is the Stream Deck MK.2 Scissor Switch revision.
// It is not yet registered in StreamDeckSharp 6.1.0 (only 0x0080 is).
// The library exposes Hardware.RegisterNewHardware() for exactly this case.
// Same JPEG driver and 5x3 key layout as the original MK.2.
Hardware.RegisterNewHardware(
    usbId: new UsbVendorProductPair(0x0FD9, 0x00A5),
    deviceName: "Stream Deck MK.2 (Scissor Switch)",
    keyLayout: new GridKeyLayout(5, 3, 72, 32),
    driver: new HidComDriverStreamDeckJpeg(72) { BytesPerSecondLimit = 1_500_000 }
);

var devices = StreamDeck.EnumerateDevices().ToList();
Console.WriteLine($"Found {devices.Count} device(s).");

if (devices.Count == 0)
{
    Console.Error.WriteLine("No devices found even after registering PID 0x00A5.");
    Console.Error.WriteLine("Check USB connection and that the device shows in Device Manager.");
    return 1;
}

using var deck = devices[0].Open();
Console.WriteLine($"Opened device (keys: {deck.Keys.Count})");

deck.SetBrightness(80);

var green = KeyBitmap.Create.FromRgb(0, 200, 0);
deck.SetKeyBitmap(0, green);

Console.WriteLine("Key 0 should be green. Waiting 5 s...");
await Task.Delay(5000);

deck.ClearKeys();
deck.SetBrightness(0);
Console.WriteLine("Done. Spike passed - proceed to Step B (Docker).");
return 0;
