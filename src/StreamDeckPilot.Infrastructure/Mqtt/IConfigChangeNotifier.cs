namespace StreamDeckPilot.Infrastructure.Mqtt;

public interface IConfigChangeNotifier
{
    Task NotifyConfigChangedAsync(string serial, CancellationToken ct = default);
}
