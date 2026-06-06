using OpenMacroBoard.SDK;
using StreamDeckSharp;
using StreamDeckSharp.Internals;

namespace StreamDeckPilot.Infrastructure.StreamDeck;

public static class StreamDeckHardwareRegistration
{
    private static int _registered;

    // PID 0x00A5 is the MK.2 Scissor Switch revision, missing from StreamDeckSharp 6.1.0.
    // Must be called before any EnumerateDevices() or OpenDevice() call.
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;

        Hardware.RegisterNewHardware(
            usbId: new UsbVendorProductPair(0x0FD9, 0x00A5),
            deviceName: "Stream Deck MK.2 (Scissor Switch)",
            keyLayout: new GridKeyLayout(5, 3, 72, 32),
            driver: new HidComDriverStreamDeckJpeg(72) { BytesPerSecondLimit = 1_500_000 }
        );
    }
}
