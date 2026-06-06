using StreamDeckPilot.Core.DeviceState;

namespace StreamDeckPilot.Infrastructure.DeviceState;

// Placeholder until Plan 04 implements real device supervision.
public sealed class NullDeviceStateProvider : IDeviceStateProvider
{
    public DeviceConnectionState GetState(string serial) => DeviceConnectionState.Unknown;
}
