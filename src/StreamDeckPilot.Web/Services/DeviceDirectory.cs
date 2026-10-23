using StreamDeckPilot.Core.DeviceState;

namespace StreamDeckPilot.Web.Services;

/// <summary>A catalogued deck as the UI lists it.</summary>
public sealed record DeviceSummary(string Serial, string Model, int KeyRows, int KeyColumns, DeviceConnectionState State)
{
    public bool IsConnected => State == DeviceConnectionState.Connected;
}

/// <summary>
/// The decks the UI can show: the catalogue (physical discovery) plus live connection state.
/// <see cref="Changed"/> fires when either changes (pushed, #108); re-read with <see cref="ListAsync"/>.
/// </summary>
public interface IDeviceDirectory
{
    Task<IReadOnlyList<DeviceSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised off the circuit's thread when the list or a deck's state changed.</summary>
    event Action? Changed;
}

/// <summary>Per circuit, backed by the circuit's <see cref="LiveState"/>.</summary>
public sealed class DeviceDirectory(LiveState live) : IDeviceDirectory
{
    public event Action? Changed
    {
        add => live.Changed += value;
        remove => live.Changed -= value;
    }

    public async Task<IReadOnlyList<DeviceSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await live.StartAsync();
        return [.. live.Snapshot!.Devices.Select(d => new DeviceSummary(d.Serial, d.Model, d.KeyRows, d.KeyColumns, d.State))];
    }
}
