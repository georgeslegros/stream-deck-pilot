using System.Collections.Concurrent;
using System.Collections.Immutable;
using StreamDeckPilot.Core.DeviceState;

namespace StreamDeckPilot.Infrastructure.Events;

/// <summary>
/// The live state a client starts from (#108): every deck, the broker, brightness and the latest
/// value per bound topic, as of <see cref="Sequence"/>. Events after that sequence are deltas;
/// <see cref="Apply"/> folds one in. Immutable, so a UI can render it from any thread.
/// </summary>
public sealed record LiveSnapshot(
    string BootId,
    long Sequence,
    BrokerSnapshot Broker,
    int Brightness,
    ImmutableArray<DeviceSnapshot> Devices,
    ImmutableDictionary<string, TopicValue> Values)
{
    public DeviceSnapshot? Device(string serial) =>
        Devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal));

    /// <summary>
    /// This snapshot with <paramref name="e"/> applied, or null when the event can't be applied
    /// (a deck that isn't in the snapshot): the caller then reloads a fresh snapshot.
    /// </summary>
    public LiveSnapshot? Apply(LiveEvent e)
    {
        var next = this with { Sequence = Math.Max(Sequence, e.Sequence) };
        switch (e)
        {
            case DeviceConnected or DeviceDisconnected or KeyRendered or PageChanged when Device(e.Serial!) is null:
                return null;
            case DeviceConnected:
                return next.WithDevice(e.Serial!, d => d with { State = DeviceConnectionState.Connected });
            case DeviceDisconnected:
                return next.WithDevice(e.Serial!, d => d with { State = DeviceConnectionState.Disconnected });
            case KeyRendered k:
                return next.WithDevice(e.Serial!, d => d with { Keys = d.Keys.SetItem(k.Key, k.Hash) });
            case PageChanged p:
                return next.WithDevice(e.Serial!, d => d with { ActivePage = p.PageId });
            case ValueReceived v:
                return next with
                {
                    Values = Values.SetItem(v.Topic, new TopicValue(v.Value, v.At)),
                    Broker = Broker with { LastMessageAt = v.At },
                };
            case BrokerOnline:
                return next with { Broker = Broker with { Connected = true, OfflineIndicator = false } };
            case BrokerOffline o:
                return next with { Broker = Broker with { Connected = false, OfflineIndicator = o.OfflineIndicator } };
            case BrightnessChanged b:
                return next with { Brightness = b.Level };
            default:
                return next;   // ConfigChanged, ConfigRejected, future types: nothing to fold in
        }
    }

    private LiveSnapshot WithDevice(string serial, Func<DeviceSnapshot, DeviceSnapshot> change) =>
        this with
        {
            Devices = [.. Devices.Select(d => string.Equals(d.Serial, serial, StringComparison.Ordinal) ? change(d) : d)],
        };
}

public sealed record BrokerSnapshot(bool Connected, bool OfflineIndicator, DateTimeOffset? LastMessageAt);

/// <summary>A catalogued deck: connection state, page on screen, and the content hash of each painted key.</summary>
public sealed record DeviceSnapshot(
    string Serial,
    string Model,
    int KeyRows,
    int KeyColumns,
    DeviceConnectionState State,
    string? ActivePage,
    ImmutableDictionary<int, string> Keys)
{
    public bool IsConnected => State == DeviceConnectionState.Connected;
}

public sealed record TopicValue(string Value, DateTimeOffset At);

/// <summary>The latest payload per bound topic since the service started, for snapshots.</summary>
public sealed class LatestValues
{
    // Bound topics only (see LiveEventBridge); the cap is a guard against a runaway config.
    private const int MaxTopics = 10_000;
    private readonly ConcurrentDictionary<string, TopicValue> _values = new(StringComparer.Ordinal);

    public void Set(string topic, TopicValue value)
    {
        if (_values.Count >= MaxTopics && !_values.ContainsKey(topic)) return;
        _values[topic] = value;
    }

    public ImmutableDictionary<string, TopicValue> ToImmutable() => _values.ToImmutableDictionary(StringComparer.Ordinal);
}
