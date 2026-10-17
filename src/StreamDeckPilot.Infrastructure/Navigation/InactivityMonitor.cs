using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Infrastructure.Navigation;

/// <summary>
/// One inactivity timer per deck (#24). Every <see cref="Touch"/> (a key press or a page change)
/// restarts it; when it runs out, <see cref="Expired"/> is raised with the serial. What "go home"
/// means is left to the subscriber. The timeout is the device config's override when it has one,
/// otherwise <see cref="NavigationOptions.HomeAfterInactivitySeconds"/>; 0 means off.
/// </summary>
public sealed class InactivityMonitor : IDisposable
{
    private readonly int _defaultSeconds;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InactivityMonitor> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public int? OverrideSeconds;
        public ITimer? Timer;
        // Bumped whenever the timer is re-armed or cancelled, so a callback that was already
        // dispatched when a press restarted the timeout can't send the deck home (same as #70).
        public long Generation;
    }

    /// <summary>Raised (outside the lock) when a deck has been idle for its timeout.</summary>
    public event Action<string>? Expired;

    public InactivityMonitor(
        IOptions<NavigationOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<InactivityMonitor>? logger = null)
    {
        _defaultSeconds = Math.Max(0, options.Value.HomeAfterInactivitySeconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<InactivityMonitor>.Instance;
    }

    /// <summary>
    /// Records the device config's override (null = use the global option) and restarts the timer.
    /// Called whenever the deck's config is (re)loaded.
    /// </summary>
    public void SetDeviceTimeout(string serial, int? seconds)
    {
        lock (_lock)
        {
            var entry = GetEntry(serial);
            entry.OverrideSeconds = seconds;
            Arm(serial, entry);
        }
    }

    /// <summary>Activity on the deck: restarts its timer.</summary>
    public void Touch(string serial)
    {
        lock (_lock) Arm(serial, GetEntry(serial));
    }

    /// <summary>The timeout in effect for a deck, or null when it is off.</summary>
    public TimeSpan? TimeoutFor(string serial)
    {
        lock (_lock)
        {
            var seconds = _entries.TryGetValue(serial, out var e) && e.OverrideSeconds is { } o ? o : _defaultSeconds;
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }
    }

    // Under _lock.
    private Entry GetEntry(string serial)
    {
        if (!_entries.TryGetValue(serial, out var entry))
            _entries[serial] = entry = new Entry();
        return entry;
    }

    // Under _lock.
    private void Arm(string serial, Entry entry)
    {
        Cancel(entry);
        var seconds = entry.OverrideSeconds ?? _defaultSeconds;
        if (seconds <= 0) return;

        var generation = entry.Generation;
        entry.Timer = _timeProvider.CreateTimer(
            _ => OnTimer(serial, generation), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    // Under _lock.
    private static void Cancel(Entry entry)
    {
        entry.Generation++;
        entry.Timer?.Dispose();
        entry.Timer = null;
    }

    private void OnTimer(string serial, long generation)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(serial, out var entry) || entry.Generation != generation) return;
            Cancel(entry); // one-shot: the next press or page change arms it again
        }

        try { Expired?.Invoke(serial); }
        catch (Exception ex) { _logger.LogWarning(ex, "Inactivity handler failed for {Serial}", serial); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values) Cancel(entry);
            _entries.Clear();
        }
    }
}
