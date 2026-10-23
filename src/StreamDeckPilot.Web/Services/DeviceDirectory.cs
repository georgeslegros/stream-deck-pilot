using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Web.Services;

/// <summary>A catalogued deck as the UI lists it.</summary>
public sealed record DeviceSummary(string Serial, string Model, int KeyRows, int KeyColumns, DeviceConnectionState State)
{
    public bool IsConnected => State == DeviceConnectionState.Connected;
}

/// <summary>The decks the UI can show: the catalogue (physical discovery) plus live connection state.</summary>
public interface IDeviceDirectory
{
    Task<IReadOnlyList<DeviceSummary>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed class DeviceDirectory(CatalogueStore catalogue, IDeviceStateProvider states) : IDeviceDirectory
{
    public async Task<IReadOnlyList<DeviceSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var cat = await catalogue.LoadAsync();
        return cat.Devices
            .OrderBy(d => d.Serial, StringComparer.Ordinal)
            .Select(d => new DeviceSummary(d.Serial, d.Model, d.KeyRows, d.KeyColumns, states.GetState(d.Serial)))
            .ToList();
    }
}
