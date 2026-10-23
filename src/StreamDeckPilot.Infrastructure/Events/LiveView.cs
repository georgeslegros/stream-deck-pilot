using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StreamDeckPilot.Infrastructure.Events;

/// <summary>
/// A live, always-current <see cref="LiveSnapshot"/> for one consumer (a UI circuit, a test): it
/// subscribes to the bus, loads a snapshot, then folds every event in and raises
/// <see cref="Changed"/>. It resynchronises from a fresh snapshot when it fell behind (the bus
/// dropped events for it) or an event names a deck it doesn't know yet. Nothing polls: updates
/// arrive as the bus delivers them. Dispose to unsubscribe.
/// </summary>
public sealed class LiveView(IEventBus bus, LiveSnapshotService snapshots, ILogger<LiveView>? logger = null) : IDisposable
{
    private readonly ILogger<LiveView> _logger = logger ?? NullLogger<LiveView>.Instance;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private EventSubscription? _subscription;
    private Task? _pump;
    private volatile LiveSnapshot? _current;

    /// <summary>The state as of the last applied event; null until <see cref="StartAsync"/> completed.</summary>
    public LiveSnapshot? Current => _current;

    /// <summary>Raised after <see cref="Current"/> changed, on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Raised with each event after it was applied, for views that react to the event itself.</summary>
    public event Action<LiveEvent>? EventApplied;

    /// <summary>Subscribes and loads the first snapshot. Safe to call more than once.</summary>
    public async Task StartAsync(EventFilter? filter = null)
    {
        await _startLock.WaitAsync();
        try
        {
            if (_subscription is not null) return;
            // Subscribe before taking the snapshot, so nothing between the two is missed.
            _subscription = bus.Subscribe(filter);
            _current = await snapshots.GetAsync(filter?.Serial);
            _pump = Task.Run(() => PumpAsync(_subscription, filter?.Serial, _cts.Token));
        }
        finally { _startLock.Release(); }
    }

    private async Task PumpAsync(EventSubscription subscription, string? serial, CancellationToken ct)
    {
        try
        {
            await foreach (var e in subscription.Reader.ReadAllAsync(ct))
            {
                if (subscription.TakeOverflow())
                {
                    // We fell behind and missed events: start over from the current state.
                    _current = await snapshots.GetAsync(serial);
                    Raise(null);
                    continue;
                }

                var current = _current!;
                if (e.Sequence <= current.Sequence) continue;   // already in the snapshot
                _current = current.Apply(e) ?? await snapshots.GetAsync(serial);
                Raise(e);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Live view stopped updating"); }
    }

    private void Raise(LiveEvent? e)
    {
        try
        {
            if (e is not null) EventApplied?.Invoke(e);
            Changed?.Invoke();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "A live view subscriber failed"); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _subscription?.Dispose();
        _cts.Dispose();
    }
}
