namespace StreamDeckPilot.Core.DeviceState;

public interface IDeviceStateProvider
{
    DeviceConnectionState GetState(string serial);
}
