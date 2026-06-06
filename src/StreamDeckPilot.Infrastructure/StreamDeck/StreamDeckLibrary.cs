using OpenMacroBoard.SDK;
using StreamDeckSharp;
using SdSharp = StreamDeckSharp.StreamDeck;

namespace StreamDeckPilot.Infrastructure.StreamDeck;

public sealed class StreamDeckLibrary : IStreamDeckLibrary
{
    public IReadOnlyList<IStreamDeckDeviceRef> Enumerate() =>
        SdSharp.EnumerateDevices()
            .Select(r => (IStreamDeckDeviceRef)new DeviceRefWrapper(r))
            .ToList();

    private sealed class DeviceRefWrapper(StreamDeckDeviceReference reference) : IStreamDeckDeviceRef
    {
        public string Path => reference.ToString() ?? string.Empty;
        public string DeviceName => reference.DeviceName;
        public IMacroBoard Open() => reference.Open();
    }
}
