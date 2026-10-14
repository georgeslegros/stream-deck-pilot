namespace StreamDeckPilot.Infrastructure.Mqtt;

public interface IConfigChangeNotifier
{
    Task NotifyConfigChangedAsync(string serial, CancellationToken ct = default);

    // Called once the device's desired state has been rebuilt from the new config, so values
    // that arrive land on the new buttons rather than being wiped by the rebuild.
    Task RefreshRetainedAsync(string serial, CancellationToken ct = default);
}
