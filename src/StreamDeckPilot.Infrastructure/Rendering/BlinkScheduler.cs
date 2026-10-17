using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Infrastructure.Rendering;

/// <summary>
/// Drives blinking keys (#23). One timer per device, running only while that device has a key whose
/// matching rule says <c>blink</c>: every interval it flips the device's phase and raises
/// <see cref="Toggled"/>, and the supervisor repaints just the blinking keys on the active page.
/// Like the broker-offline overlay, the phase is applied at paint time, so desired state never
/// changes. When the last blinking key stops blinking the timer is disposed and the phase resets,
/// so the next blink starts on its normal look.
/// </summary>
public sealed class BlinkScheduler : IDisposable
{
    private readonly DesiredStateStore _store;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly ILogger<BlinkScheduler>? _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DeviceBlink> _devices = new();

    private sealed class DeviceBlink(ITimer timer)
    {
        public ITimer Timer { get; } = timer;
        public bool OffPhase { get; set; }
    }

    public BlinkScheduler(DesiredStateStore store, IOptions<BlinkOptions> options, TimeProvider? timeProvider = null,
        ILogger<BlinkScheduler>? logger = null)
    {
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
        _interval = TimeSpan.FromMilliseconds(Math.Max(BlinkOptions.MinIntervalMilliseconds, options.Value.IntervalMilliseconds));
        _logger = logger;
        _store.Changed += Refresh;
    }

    public TimeSpan Interval => _interval;

    /// <summary>Raised (outside the lock) with the serial after its phase flipped; subscribers repaint.</summary>
    public event Action<string>? Toggled;

    /// <summary>True while blinking keys of <paramref name="serial"/> should be drawn in their dark half.</summary>
    public bool IsOffPhase(string serial)
    {
        lock (_lock) return _devices.TryGetValue(serial, out var d) && d.OffPhase;
    }

    /// <summary>True while a blink timer runs for <paramref name="serial"/>.</summary>
    public bool IsRunning(string serial)
    {
        lock (_lock) return _devices.ContainsKey(serial);
    }

    /// <summary>Starts or stops the device's timer to match whether any of its keys blink now.</summary>
    public void Refresh(string serial)
    {
        lock (_lock)
        {
            // Read under the lock so two writers racing can't leave the timer in the wrong state.
            var blinking = _store.AnyBlinking(serial);
            var running = _devices.TryGetValue(serial, out var device);
            if (blinking == running) return;

            if (blinking)
            {
                // Created stopped and armed once stored, so the first tick always finds its entry.
                DeviceBlink? entry = null;
                entry = new DeviceBlink(_time.CreateTimer(_ => Tick(serial, entry!), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
                _devices[serial] = entry;
                entry.Timer.Change(_interval, _interval);
                _logger?.LogDebug("Blink started for {Serial}", serial);
            }
            else
            {
                device!.Timer.Dispose();
                _devices.Remove(serial);
                _logger?.LogDebug("Blink stopped for {Serial}", serial);
            }
        }
    }

    private void Tick(string serial, DeviceBlink owner)
    {
        lock (_lock)
        {
            // A callback already dispatched when its timer was disposed finds no entry, or a newer
            // one it doesn't own, and does nothing.
            if (!_devices.TryGetValue(serial, out var device) || !ReferenceEquals(device, owner)) return;
            owner.OffPhase = !owner.OffPhase;
        }
        Toggled?.Invoke(serial);
    }

    public void Dispose()
    {
        _store.Changed -= Refresh;
        lock (_lock)
        {
            foreach (var d in _devices.Values) d.Timer.Dispose();
            _devices.Clear();
        }
    }
}
