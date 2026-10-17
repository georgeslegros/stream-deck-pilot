using Microsoft.Extensions.Logging;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Mqtt;

namespace StreamDeckPilot.Infrastructure.Rendering;

/// <summary>
/// Immediate press feedback (#22). A press that publishes marks its key pending; the key's next
/// inbound value clears it. If no value arrives within <see cref="PressFeedbackOptions.TimeoutSeconds"/>
/// the key flashes red for <see cref="PressFeedbackOptions.FlashMilliseconds"/>, then returns to
/// normal. A key with no inbound topic shows pending for the flash duration only, since nothing
/// could confirm it. While the broker is offline (#26) there is no feedback at all: the offline
/// overlay already explains why nothing happens.
/// A render-time overlay like <see cref="BrokerIndicator"/>: desired state is never changed, the
/// renderer consults <see cref="Get"/> while painting, and <see cref="Changed"/> asks for a repaint.
/// </summary>
public sealed class PressFeedbackTracker : IDisposable
{
    private sealed class Entry
    {
        public required string PageId { get; init; }
        public required int KeyIndex { get; init; }
        public required int Generation { get; init; }
        public required bool CanFail { get; init; }
        public PressFeedback State { get; set; } = PressFeedback.Pending;
        public ITimer? Timer { get; set; }
    }

    private readonly PressFeedbackOptions _opts;
    private readonly TimeProvider _time;
    private readonly BrokerIndicator? _broker;
    private readonly ILogger<PressFeedbackTracker>? _logger;
    private readonly Dictionary<(string Serial, string ButtonId), Entry> _entries = new();
    private int _generation; // a late callback from a replaced or cleared entry's timer is ignored

    public PressFeedbackTracker(PressFeedbackOptions options, TimeProvider? timeProvider = null,
        BrokerIndicator? broker = null, ILogger<PressFeedbackTracker>? logger = null)
    {
        _opts = options;
        _time = timeProvider ?? TimeProvider.System;
        _broker = broker;
        _logger = logger;
        if (broker is not null)
            broker.Changed += offline => { if (offline) ClearAll(); };
    }

    /// <summary>Raised (outside the lock) with (serial, pageId, keyIndex) when a key's feedback changes.</summary>
    public event Action<string, string, int>? Changed;

    /// <summary>The feedback to draw on a button now.</summary>
    public PressFeedback Get(string serial, string buttonId)
    {
        lock (_entries)
            return _entries.TryGetValue((serial, buttonId), out var e) ? e.State : PressFeedback.None;
    }

    /// <summary>
    /// A press on this button published something. Applies the button's own
    /// <see cref="DisplaySpec.PressFeedback"/> mode and timeout over the global options.
    /// </summary>
    public void Pressed(string serial, string pageId, ButtonDefinition button)
    {
        var mode = button.Display?.PressFeedback ?? PressFeedbackModes.Auto;
        if (mode == PressFeedbackModes.Off) return;
        var timeout = button.Display?.PressFeedbackTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        Pressed(serial, pageId, button.KeyIndex, button.ButtonId,
            awaitsState: button.Inbound is not null, canFail: mode == PressFeedbackModes.Auto, timeout);
    }

    /// <summary>
    /// A press on this button published something. <paramref name="awaitsState"/> is true when the
    /// button has an inbound topic whose next value confirms the press; <paramref name="canFail"/>
    /// false ends the wait at the timeout without the red flash ("ring" mode).
    /// </summary>
    public void Pressed(string serial, string pageId, int keyIndex, string buttonId, bool awaitsState,
        bool canFail = true, TimeSpan? timeout = null)
    {
        if (!_opts.Enabled || _broker?.ShowOffline == true) return;

        var key = (serial, buttonId);
        lock (_entries)
        {
            if (_entries.Remove(key, out var old)) old.Timer?.Dispose();
            var entry = new Entry { PageId = pageId, KeyIndex = keyIndex, Generation = ++_generation, CanFail = canFail };
            entry.Timer = awaitsState
                ? _time.CreateTimer(_ => OnTimeout(key, entry.Generation), null, timeout ?? Timeout(), System.Threading.Timeout.InfiniteTimeSpan)
                : _time.CreateTimer(_ => Clear(key, entry.Generation), null, Flash(), System.Threading.Timeout.InfiniteTimeSpan);
            _entries[key] = entry;
        }
        Changed?.Invoke(serial, pageId, keyIndex);
    }

    /// <summary>
    /// A new value arrived for this button: the press is confirmed (or, after a red flash, late but
    /// answered). Raises no event: the caller is about to paint the new value anyway.
    /// </summary>
    public void StateReceived(string serial, string buttonId)
    {
        lock (_entries)
            if (_entries.Remove((serial, buttonId), out var e)) e.Timer?.Dispose();
    }

    private void OnTimeout((string Serial, string ButtonId) key, int generation)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!) || entry.Generation != generation) return;
            entry.Timer?.Dispose();
            if (!entry.CanFail || _broker?.ShowOffline == true)
            {
                // "ring" mode just stops waiting. And if the broker went away while waiting, that's
                // not this key's failure.
                _entries.Remove(key);
            }
            else
            {
                entry.State = PressFeedback.Failed;
                entry.Timer = _time.CreateTimer(_ => Clear(key, generation), null, Flash(), System.Threading.Timeout.InfiniteTimeSpan);
            }
        }
        if (entry.State == PressFeedback.Failed)
            _logger?.LogWarning("No state for button {ButtonId} on {Serial} within the press feedback timeout",
                key.ButtonId, key.Serial);
        Changed?.Invoke(key.Serial, entry.PageId, entry.KeyIndex);
    }

    private void Clear((string Serial, string ButtonId) key, int generation)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!) || entry.Generation != generation) return;
            entry.Timer?.Dispose();
            _entries.Remove(key);
        }
        Changed?.Invoke(key.Serial, entry.PageId, entry.KeyIndex);
    }

    private void ClearAll()
    {
        List<(string Serial, Entry Entry)> cleared;
        lock (_entries)
        {
            cleared = [.. _entries.Select(kv => (kv.Key.Serial, kv.Value))];
            foreach (var (_, e) in cleared) e.Timer?.Dispose();
            _entries.Clear();
        }
        foreach (var (serial, e) in cleared) Changed?.Invoke(serial, e.PageId, e.KeyIndex);
    }

    private TimeSpan Timeout() => TimeSpan.FromSeconds(Math.Max(0, _opts.TimeoutSeconds));
    private TimeSpan Flash() => TimeSpan.FromMilliseconds(Math.Max(0, _opts.FlashMilliseconds));

    public void Dispose()
    {
        lock (_entries)
        {
            foreach (var e in _entries.Values) e.Timer?.Dispose();
            _entries.Clear();
        }
    }
}
