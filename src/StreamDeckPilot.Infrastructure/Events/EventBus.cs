using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Infrastructure.Events;

/// <summary>
/// The one in-process source of live events (#108). Publishers (device runtime, MQTT, config, the
/// renderer) call <see cref="Publish"/>, which never blocks; each subscriber reads its own bounded
/// channel at its own pace.
/// </summary>
public interface IEventBus
{
    /// <summary>Changes on every start, so a client can tell its resume point belongs to an earlier run.</summary>
    string BootId { get; }

    /// <summary>Sequence of the last delivered event (0 before the first).</summary>
    long LastSequence { get; }

    /// <summary>Delivers an event to every matching subscriber, or holds it back for coalescing.</summary>
    void Publish(LiveEvent e);

    /// <summary>A new subscriber. Dispose it to stop receiving.</summary>
    EventSubscription Subscribe(EventFilter? filter = null);

    /// <summary>
    /// The matching events delivered after <paramref name="afterSequence"/>, if all of them are
    /// still in the replay buffer, and the sequence they bring the client up to (<paramref name="upTo"/>,
    /// which can be past the last returned event when later ones didn't match the filter). False
    /// means the client must resynchronise from a snapshot.
    /// </summary>
    bool TryReplay(long afterSequence, EventFilter filter, out IReadOnlyList<LiveEvent> events, out long upTo);
}

/// <summary>
/// One subscriber's view of the bus: a bounded channel of the events that match its filter. When
/// it is full, new events are dropped and <see cref="TakeOverflow"/> turns true once, telling the
/// reader to resynchronise from a fresh snapshot. A slow reader never slows the publishers.
/// </summary>
public sealed class EventSubscription : IDisposable
{
    private readonly EventBus _bus;
    private readonly Channel<LiveEvent> _channel;
    private int _overflowed;

    internal EventSubscription(EventBus bus, EventFilter filter, int capacity)
    {
        _bus = bus;
        Filter = filter;
        _channel = Channel.CreateBounded<LiveEvent>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            _ => Interlocked.Exchange(ref _overflowed, 1));
    }

    public EventFilter Filter { get; }

    public ChannelReader<LiveEvent> Reader => _channel.Reader;

    /// <summary>True (once) if events were dropped since the last call.</summary>
    public bool TakeOverflow() => Interlocked.Exchange(ref _overflowed, 0) == 1;

    internal void Offer(LiveEvent e)
    {
        if (Filter.Matches(e)) _channel.Writer.TryWrite(e);
    }

    public void Dispose()
    {
        _bus.Remove(this);
        _channel.Writer.TryComplete();
    }
}

public sealed class EventBus : IEventBus, IDisposable
{
    private readonly EventsOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<EventBus> _logger;
    private readonly TimeSpan _coalesceInterval;

    // Sequence, replay buffer and fan-out under one lock, so every subscriber sees events in
    // sequence order. Offer only does a non-blocking TryWrite.
    private readonly Lock _lock = new();
    private long _sequence;
    private EventSubscription[] _subscribers = [];
    private readonly LiveEvent?[] _replay;
    private int _replayCount;

    private readonly Lock _coalesceLock = new();
    private readonly Dictionary<string, CoalesceSlot> _slots = new(StringComparer.Ordinal);
    private bool _disposed;

    private sealed class CoalesceSlot
    {
        public DateTimeOffset LastDelivered = DateTimeOffset.MinValue;
        public LiveEvent? Pending;
        public ITimer? Timer;
    }

    public EventBus(IOptions<EventsOptions> options, TimeProvider? time = null, ILogger<EventBus>? logger = null)
    {
        _options = options.Value;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<EventBus>.Instance;
        _coalesceInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _options.MaxPerSecondPerKey));
        _replay = new LiveEvent?[Math.Max(0, _options.ReplayCapacity)];
    }

    public string BootId { get; } = Guid.NewGuid().ToString("N")[..12];

    public long LastSequence
    {
        get { lock (_lock) return _sequence; }
    }

    public void Publish(LiveEvent e)
    {
        try
        {
            if (e.CoalesceKey is { } key) Coalesce(key, e);
            else Deliver(e);
        }
        catch (Exception ex)
        {
            // Publishers sit on the render path and in MQTT handlers: an event must never break them.
            _logger.LogWarning(ex, "Could not publish live event {Type}", e.Type);
        }
    }

    public EventSubscription Subscribe(EventFilter? filter = null)
    {
        var subscription = new EventSubscription(this, filter ?? EventFilter.All, Math.Max(16, _options.SubscriberCapacity));
        lock (_lock) _subscribers = [.. _subscribers, subscription];
        return subscription;
    }

    internal void Remove(EventSubscription subscription)
    {
        lock (_lock) _subscribers = [.. _subscribers.Where(s => !ReferenceEquals(s, subscription))];
    }

    public bool TryReplay(long afterSequence, EventFilter filter, out IReadOnlyList<LiveEvent> events, out long upTo)
    {
        lock (_lock)
        {
            events = [];
            upTo = _sequence;
            if (afterSequence > _sequence || afterSequence < 0) return false;   // from another run, or bogus
            if (afterSequence == _sequence) return true;
            var oldest = _sequence - _replayCount + 1;
            if (afterSequence + 1 < oldest) return false;                       // gap: fell out of the buffer

            var list = new List<LiveEvent>();
            for (var seq = afterSequence + 1; seq <= _sequence; seq++)
            {
                var e = _replay[seq % _replay.Length]!;
                if (filter.Matches(e)) list.Add(e);
            }
            events = list;
            return true;
        }
    }

    private void Deliver(LiveEvent e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            e = e with { Sequence = ++_sequence, At = _time.GetUtcNow() };
            if (_replay.Length > 0)
            {
                _replay[e.Sequence % _replay.Length] = e;
                _replayCount = Math.Min(_replayCount + 1, _replay.Length);
            }
            foreach (var subscriber in _subscribers)
                subscriber.Offer(e);
        }
    }

    // Latest wins, at most one delivery per key per interval. The first event of a quiet key goes
    // out at once; later ones within the interval wait, replacing each other, and the survivor is
    // delivered when the interval ends (a one-shot timer, not a poll).
    private void Coalesce(string key, LiveEvent e)
    {
        var deliverNow = false;
        lock (_coalesceLock)
        {
            if (_disposed) return;
            if (!_slots.TryGetValue(key, out var slot))
                _slots[key] = slot = new CoalesceSlot();

            var now = _time.GetUtcNow();
            if (slot.Timer is null && now - slot.LastDelivered >= _coalesceInterval)
            {
                slot.LastDelivered = now;
                deliverNow = true;
            }
            else
            {
                slot.Pending = e;
                if (slot.Timer is null)
                {
                    var due = slot.LastDelivered + _coalesceInterval - now;
                    slot.Timer = _time.CreateTimer(_ => Flush(key), null,
                        due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
                }
            }
        }
        if (deliverNow) Deliver(e);
    }

    private void Flush(string key)
    {
        LiveEvent? e;
        lock (_coalesceLock)
        {
            if (_disposed || !_slots.TryGetValue(key, out var slot)) return;
            e = slot.Pending;
            slot.Pending = null;
            slot.Timer?.Dispose();
            slot.Timer = null;
            slot.LastDelivered = _time.GetUtcNow();
        }
        if (e is not null)
        {
            try { Deliver(e); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not deliver coalesced live event {Type}", e.Type); }
        }
    }

    public void Dispose()
    {
        EventSubscription[] subscribers;
        lock (_coalesceLock)
        {
            _disposed = true;
            foreach (var slot in _slots.Values) slot.Timer?.Dispose();
            _slots.Clear();
        }
        lock (_lock)
        {
            _disposed = true;
            subscribers = _subscribers;
        }
        foreach (var s in subscribers) s.Dispose();
    }
}
