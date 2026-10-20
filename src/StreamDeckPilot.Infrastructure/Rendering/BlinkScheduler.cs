using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Infrastructure.Rendering;

/// <summary>
/// Drives blinking keys (#23, #131). One timer per device, running only while that device has a key
/// whose matching rule blinks. The timer ticks at the finest frame any of those keys needs (a flash
/// changes twice per cycle, a pulse 4–12 times, never more often than every 100 ms) and advances the
/// device's blink clock; each key's frame is derived from that clock and its own style and period.
/// On a tick <see cref="Toggled"/> fires and the supervisor repaints only the keys on the active page
/// whose own frame changed (<see cref="ShouldRepaint"/>). Like the broker-offline overlay, the frame
/// is applied at paint time, so desired state never changes. When the last key stops blinking the
/// timer is disposed and the clock resets, so the next blink starts on its normal look.
/// </summary>
public sealed class BlinkScheduler : IDisposable
{
    private readonly DesiredStateStore _store;
    private readonly TimeProvider _time;
    private readonly BlinkOptions _options;
    private readonly ILogger<BlinkScheduler>? _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DeviceBlink> _devices = new();

    private sealed class DeviceBlink(ITimer timer, long tickMs)
    {
        public ITimer Timer { get; } = timer;
        public long TickMs { get; set; } = tickMs;
        public long ElapsedMs { get; set; }
        // The frame each key was last painted with, so a tick repaints only keys whose frame moved.
        public Dictionary<int, int> PaintedSteps { get; } = new();
    }

    /// <summary>A blink with every default filled in.</summary>
    public readonly record struct ResolvedBlink(bool Pulse, float Strength, int PeriodMs, int Steps)
    {
        public double StepMs => (double)PeriodMs / Steps;

        /// <summary>Brightness kept in frame <paramref name="step"/>: 1 is the normal look.</summary>
        public float LevelAt(int step)
        {
            if (!Pulse) return step == 1 ? Strength : 1f;
            // Cosine ease: frame 0 is the normal look, the middle frame the dimmest. Rounded so the
            // two halves of the cycle share frames (and cached bitmaps).
            var fade = (1 + Math.Cos(2 * Math.PI * step / Steps)) / 2;
            return (float)Math.Round(Strength + (1 - Strength) * fade, 3);
        }
    }

    public BlinkScheduler(DesiredStateStore store, IOptions<BlinkOptions> options, TimeProvider? timeProvider = null,
        ILogger<BlinkScheduler>? logger = null)
    {
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
        _options = options.Value;
        _logger = logger;
        _store.Changed += Refresh;
    }

    /// <summary>How long one frame of a default (<c>"blink": true</c>) key lasts.</summary>
    public TimeSpan Interval => TimeSpan.FromMilliseconds(Resolve(BlinkSpec.On).StepMs);

    /// <summary>Raised (outside the lock) with the serial after its blink clock advanced; subscribers repaint.</summary>
    public event Action<string>? Toggled;

    /// <summary>Fills in the global defaults for anything <paramref name="spec"/> doesn't override, clamped to the supported ranges.</summary>
    public ResolvedBlink Resolve(BlinkSpec spec)
    {
        var style = spec.Style ?? _options.Style;
        var pulse = string.Equals(style, BlinkSpec.Pulse, StringComparison.OrdinalIgnoreCase);
        var strength = spec.Strength ?? (pulse ? _options.PulseStrength : _options.FlashStrength);
        var period = spec.PeriodMs is { } p
            ? Math.Clamp(p, BlinkSpec.MinPeriodMs, BlinkSpec.MaxPeriodMs)
            : pulse ? _options.EffectivePulsePeriodMilliseconds : _options.EffectivePeriodMilliseconds;
        var steps = pulse ? Math.Clamp(period / BlinkOptions.MinFrameMilliseconds, 2, BlinkOptions.MaxPulseSteps) : 2;
        return new ResolvedBlink(pulse, (float)Math.Clamp(strength, BlinkSpec.MinStrength, BlinkSpec.MaxStrength), period, steps);
    }

    /// <summary>The brightness a key blinking with <paramref name="spec"/> shows right now (1 when the device isn't blinking).</summary>
    public float Level(string serial, BlinkSpec spec)
    {
        var blink = Resolve(spec);
        lock (_lock)
            return _devices.TryGetValue(serial, out var d) ? blink.LevelAt(StepAt(d, blink)) : 1f;
    }

    /// <summary>
    /// The level to paint key <paramref name="keyIndex"/> with now, remembering the frame so the next
    /// tick only repaints the key once its frame changes. Called by the renderer for blinking keys.
    /// </summary>
    public float PaintLevel(string serial, int keyIndex, BlinkSpec spec)
    {
        var blink = Resolve(spec);
        lock (_lock)
        {
            if (!_devices.TryGetValue(serial, out var d)) return 1f;
            var step = StepAt(d, blink);
            d.PaintedSteps[keyIndex] = step;
            return blink.LevelAt(step);
        }
    }

    /// <summary>True when key <paramref name="keyIndex"/>'s frame differs from the one it was last painted with.</summary>
    public bool ShouldRepaint(string serial, int keyIndex, BlinkSpec spec)
    {
        var blink = Resolve(spec);
        lock (_lock)
        {
            if (!_devices.TryGetValue(serial, out var d)) return false;
            return !d.PaintedSteps.TryGetValue(keyIndex, out var painted) || painted != StepAt(d, blink);
        }
    }

    /// <summary>True while blinking keys of <paramref name="serial"/> with the default settings are in a dimmed frame.</summary>
    public bool IsOffPhase(string serial) => Level(serial, BlinkSpec.On) < 1f;

    /// <summary>True while a blink timer runs for <paramref name="serial"/>.</summary>
    public bool IsRunning(string serial)
    {
        lock (_lock) return _devices.ContainsKey(serial);
    }

    /// <summary>The device's current tick, or null when it isn't blinking.</summary>
    public TimeSpan? TickInterval(string serial)
    {
        lock (_lock) return _devices.TryGetValue(serial, out var d) ? TimeSpan.FromMilliseconds(d.TickMs) : null;
    }

    /// <summary>Starts, retunes or stops the device's timer to match the keys that blink now.</summary>
    public void Refresh(string serial)
    {
        lock (_lock)
        {
            // Read under the lock so two writers racing can't leave the timer in the wrong state.
            var specs = _store.BlinkingSpecs(serial);
            var running = _devices.TryGetValue(serial, out var device);

            if (specs.Count == 0)
            {
                if (!running) return;
                device!.Timer.Dispose();
                _devices.Remove(serial);
                _logger?.LogDebug("Blink stopped for {Serial}", serial);
                return;
            }

            // The finest frame any blinking key needs, never under the frame floor.
            var tickMs = Math.Max(BlinkOptions.MinFrameMilliseconds,
                (long)Math.Floor(specs.Distinct().Min(s => Resolve(s).StepMs)));

            if (!running)
            {
                // Created stopped and armed once stored, so the first tick always finds its entry.
                DeviceBlink? entry = null;
                entry = new DeviceBlink(_time.CreateTimer(_ => Tick(serial, entry!), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan), tickMs);
                _devices[serial] = entry;
                entry.Timer.Change(TimeSpan.FromMilliseconds(tickMs), TimeSpan.FromMilliseconds(tickMs));
                _logger?.LogDebug("Blink started for {Serial} every {TickMs} ms", serial, tickMs);
            }
            else if (device!.TickMs != tickMs)
            {
                device.TickMs = tickMs;
                device.Timer.Change(TimeSpan.FromMilliseconds(tickMs), TimeSpan.FromMilliseconds(tickMs));
            }
        }
    }

    private static int StepAt(DeviceBlink device, ResolvedBlink blink) =>
        (int)((long)Math.Floor(device.ElapsedMs / blink.StepMs) % blink.Steps);

    private void Tick(string serial, DeviceBlink owner)
    {
        lock (_lock)
        {
            // A callback already dispatched when its timer was disposed finds no entry, or a newer
            // one it doesn't own, and does nothing.
            if (!_devices.TryGetValue(serial, out var device) || !ReferenceEquals(device, owner)) return;
            owner.ElapsedMs += owner.TickMs;
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
