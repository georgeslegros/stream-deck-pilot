using Microsoft.Extensions.Logging;

namespace StreamDeckPilot.Infrastructure.Mqtt;

/// <summary>
/// Whether the deck should show the broker-offline look (#26). The MQTT service reports
/// connection changes; the indicator turns on only after the broker has been gone for
/// <c>OfflineIndicatorDelaySeconds</c>, so a brief blip during a reconnect doesn't flash every
/// key. It turns off as soon as the session is back. A render-time overlay: desired state is
/// never changed, and the renderer consults <see cref="ShowOffline"/> while painting.
/// </summary>
public sealed class BrokerIndicator
{
    private readonly TimeSpan _delay;
    private readonly TimeProvider _time;
    private readonly ILogger<BrokerIndicator>? _logger;
    private readonly object _lock = new();
    private ITimer? _pending;
    private int _generation; // bumped on every arm/disarm, so a late callback from an old timer is ignored
    private volatile bool _showOffline;

    public BrokerIndicator(TimeSpan delay, TimeProvider? timeProvider = null, ILogger<BrokerIndicator>? logger = null)
    {
        _delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>True while broker-dependent keys should be drawn offline.</summary>
    public bool ShowOffline => _showOffline;

    /// <summary>Raised (outside the lock) when <see cref="ShowOffline"/> flips; subscribers repaint.</summary>
    public event Action<bool>? Changed;

    /// <summary>The broker is unreachable. Idempotent: repeated reports keep the first deadline.</summary>
    public void ReportDisconnected()
    {
        int generation;
        lock (_lock)
        {
            if (_showOffline || _pending is not null) return;
            generation = ++_generation;
            if (_delay > TimeSpan.Zero)
            {
                _pending = _time.CreateTimer(_ => OnDelayElapsed(generation), null, _delay, Timeout.InfiniteTimeSpan);
                return;
            }
        }
        OnDelayElapsed(generation, immediate: true); // no debounce configured
    }

    /// <summary>The session is back (connected and subscribed).</summary>
    public void ReportConnected()
    {
        bool cleared;
        lock (_lock)
        {
            _pending?.Dispose();
            _pending = null;
            _generation++;
            cleared = _showOffline;
            _showOffline = false;
        }
        if (!cleared) return;
        _logger?.LogInformation("MQTT broker back: offline indicator cleared");
        Changed?.Invoke(false);
    }

    private void OnDelayElapsed(int generation, bool immediate = false)
    {
        lock (_lock)
        {
            // A reconnect inside the delay disarmed this timer; its late callback must not turn it on.
            if (generation != _generation || (_pending is null && !immediate) || _showOffline) return;
            _pending?.Dispose();
            _pending = null;
            _showOffline = true;
        }
        _logger?.LogWarning("MQTT broker unreachable for {Delay:0.#}s: showing the offline indicator on broker-dependent keys",
            _delay.TotalSeconds);
        Changed?.Invoke(true);
    }
}
