using System.Collections.Immutable;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;

namespace StreamDeckPilot.Infrastructure.Events;

/// <summary>Builds the <see cref="LiveSnapshot"/> a live client starts from (#108).</summary>
public sealed class LiveSnapshotService(
    IEventBus bus,
    CatalogueStore catalogue,
    IDeviceStateProvider states,
    ActivePageStore pages,
    PaintedFrameStore frames,
    BrightnessController brightness,
    IBrokerSession broker,
    BrokerIndicator indicator,
    LatestValues values)
{
    /// <summary>
    /// The current state, optionally for one deck. Its sequence is read first, so any event after
    /// it is safe to apply on top (at worst it repeats something the snapshot already shows).
    /// </summary>
    public async Task<LiveSnapshot> GetAsync(string? serial = null)
    {
        var sequence = bus.LastSequence;
        var cat = await catalogue.LoadAsync();

        var devices = cat.Devices
            .Where(d => serial is null || string.Equals(d.Serial, serial, StringComparison.Ordinal))
            .OrderBy(d => d.Serial, StringComparer.Ordinal)
            .Select(d => new DeviceSnapshot(
                d.Serial, d.Model, d.KeyRows, d.KeyColumns,
                states.GetState(d.Serial),
                pages.GetActivePage(d.Serial),
                Keys(d.Serial, d.KeyCount)))
            .ToImmutableArray();

        var latest = values.ToImmutable();
        DateTimeOffset? lastMessage = latest.Count == 0 ? null : latest.Values.Max(v => v.At);
        return new LiveSnapshot(
            bus.BootId, sequence,
            new BrokerSnapshot(broker.IsSessionUp, indicator.ShowOffline, lastMessage),
            brightness.Effective, devices, latest);
    }

    private ImmutableDictionary<int, string> Keys(string serial, int keyCount)
    {
        var keys = ImmutableDictionary.CreateBuilder<int, string>();
        for (var k = 0; k < keyCount; k++)
            if (frames.Get(serial, k) is { } frame)
                keys[k] = frame.Hash;
        return keys.ToImmutable();
    }
}
