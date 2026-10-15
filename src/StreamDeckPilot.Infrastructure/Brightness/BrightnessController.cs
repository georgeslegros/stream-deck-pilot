using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Infrastructure.Brightness;

/// <summary>
/// Desired brightness for every deck. Scheduling lives in Home Assistant; this only holds the last
/// requested level (default 80) plus the temporary wake-on-press override while it is 0.
/// <see cref="Effective"/> is what should be on the hardware right now.
/// </summary>
public sealed class BrightnessController : IDisposable
{
    public const int DefaultLevel = 80;

    private readonly ILogger<BrightnessController> _logger;
    private readonly TimeSpan _wakeTimeout;
    private readonly TimeProvider _timeProvider;
    private ITimer? _wakeTimer;
    // Bumped every time the wake timer is armed or cancelled. Each timer carries the generation it
    // was armed with, so a callback that was already dispatched when a press re-armed the timeout
    // can't end the wake period the press just extended (#70). ITimer.Change can't recall it.
    private long _wakeGeneration;
    private readonly Lock _lock = new();

    private int _desired = DefaultLevel;
    private int _lastNonZero = DefaultLevel;
    private bool _awake; // wake-on-press override active (only ever while _desired == 0)

    // Raised (outside the lock) with the new effective level whenever it changes.
    public event Action<int>? EffectiveChanged;

    public BrightnessController(
        IOptions<BrightnessOptions> options,
        ILogger<BrightnessController>? logger = null,
        TimeProvider? timeProvider = null)
    {
        Options = options.Value;
        _logger = logger ?? NullLogger<BrightnessController>.Instance;
        _wakeTimeout = TimeSpan.FromSeconds(Math.Max(1, Options.WakeTimeoutSeconds));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BrightnessOptions Options { get; }

    public int Effective
    {
        get { lock (_lock) return EffectiveUnlocked; }
    }

    private int EffectiveUnlocked => _awake ? _lastNonZero : _desired;

    /// <summary>Applies a level requested over MQTT. A different value ends any wake period.</summary>
    public void SetDesired(int level)
    {
        level = Math.Clamp(level, 0, 100);
        int before, after;
        lock (_lock)
        {
            // The retained set topic is redelivered on every broker reconnect. Re-receiving the
            // current level is not "something else changed it", so it must not cut a wake short.
            if (level == _desired) return;

            before = EffectiveUnlocked;
            _desired = level;
            if (level > 0) _lastNonZero = level;
            if (_awake)
            {
                _awake = false;
                CancelWakeTimer();
            }
            after = EffectiveUnlocked;
        }
        _logger.LogInformation("Brightness set to {Level}", level);
        RaiseIfChanged(before, after);
    }

    /// <summary>
    /// Called for every key-down. Returns true when the press must be swallowed because it woke a
    /// dark deck. While awake, presses work normally and restart the wake timeout.
    /// </summary>
    public bool OnKeyPressed()
    {
        int before, after;
        lock (_lock)
        {
            if (_desired != 0) return false;

            ArmWakeTimer();
            if (_awake) return false;

            before = EffectiveUnlocked;
            _awake = true;
            after = EffectiveUnlocked;
        }
        _logger.LogInformation("Key press woke the deck to {Level} for {Timeout}", after, _wakeTimeout);
        RaiseIfChanged(before, after);
        return true;
    }

    // Both called under _lock.
    private void ArmWakeTimer()
    {
        CancelWakeTimer();
        _wakeTimer = _timeProvider.CreateTimer(
            state => EndWake((long)state!), _wakeGeneration, _wakeTimeout, Timeout.InfiniteTimeSpan);
    }

    private void CancelWakeTimer()
    {
        _wakeGeneration++;
        _wakeTimer?.Dispose();
        _wakeTimer = null;
    }

    private void EndWake(long generation)
    {
        int before, after;
        lock (_lock)
        {
            if (!_awake || generation != _wakeGeneration) return;
            before = EffectiveUnlocked;
            _awake = false;
            after = EffectiveUnlocked;
        }
        _logger.LogInformation("Wake period over — brightness back to {Level}", after);
        RaiseIfChanged(before, after);
    }

    private void RaiseIfChanged(int before, int after)
    {
        if (before == after) return;
        try { EffectiveChanged?.Invoke(after); }
        catch (Exception ex) { _logger.LogWarning(ex, "Brightness change handler failed"); }
    }

    /// <summary>
    /// Parses a set-topic payload: a bare number (<c>42</c>, <c>42.5</c>) or <c>{"value": n}</c>
    /// where n is a number or numeric string. Values are rounded; clamping is left to the caller.
    /// </summary>
    public static bool TryParse(string payload, out int level)
    {
        level = 0;
        var text = payload.Trim();
        if (text.Length == 0) return false;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return TryRound(number, out level);

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("value", out var value))
                return false;

            return value.ValueKind switch
            {
                JsonValueKind.Number => TryRound(value.GetDouble(), out level),
                JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s) => TryRound(s, out level),
                _ => false,
            };
        }
        catch (JsonException) { return false; }
    }

    private static bool TryRound(double value, out int level)
    {
        level = 0;
        if (!double.IsFinite(value)) return false;
        level = (int)Math.Round(Math.Clamp(value, int.MinValue, int.MaxValue));
        return true;
    }

    public void Dispose()
    {
        lock (_lock) CancelWakeTimer();
    }
}
