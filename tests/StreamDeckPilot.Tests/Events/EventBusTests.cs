using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamDeckPilot.Infrastructure.Events;

namespace StreamDeckPilot.Tests.Events;

/// <summary>The in-process event bus (#108): fan-out, filters, slow subscribers, coalescing, replay, disposal.</summary>
public sealed class EventBusTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly List<EventBus> _buses = [];

    public void Dispose()
    {
        foreach (var bus in _buses) bus.Dispose();
    }

    private EventBus Bus(int maxPerSecond = 4, int capacity = 1024, int replay = 1024)
    {
        var bus = new EventBus(Options.Create(new EventsOptions
        {
            MaxPerSecondPerKey = maxPerSecond,
            SubscriberCapacity = capacity,
            ReplayCapacity = replay,
        }), _time);
        _buses.Add(bus);
        return bus;
    }

    private static List<LiveEvent> Drain(EventSubscription subscription)
    {
        var list = new List<LiveEvent>();
        while (subscription.Reader.TryRead(out var e)) list.Add(e);
        return list;
    }

    // ── Fan-out and filters ─────────────────────────────────────────────────

    [Fact]
    public void EverySubscriber_GetsEveryEvent_InSequenceOrder()
    {
        var bus = Bus();
        using var a = bus.Subscribe();
        using var b = bus.Subscribe();

        bus.Publish(new DeviceConnected { Serial = "A" });
        bus.Publish(new PageChanged("main") { Serial = "A" });
        bus.Publish(new BrokerOnline());

        foreach (var subscription in new[] { a, b })
        {
            var events = Drain(subscription);
            Assert.Equal(["DeviceConnected", "PageChanged", "BrokerOnline"], events.Select(e => e.Type));
            Assert.Equal([1L, 2L, 3L], events.Select(e => e.Sequence));
            Assert.All(events, e => Assert.Equal(_time.GetUtcNow(), e.At));
        }
        Assert.Equal(3, bus.LastSequence);
    }

    [Fact]
    public void Filter_ByTypeAndSerial_LetsServiceWideEventsThrough()
    {
        var bus = Bus();
        var filter = EventFilter.Parse("deviceconnected, BrokerOnline", "A", out var error);
        Assert.Null(error);
        using var subscription = bus.Subscribe(filter);

        bus.Publish(new DeviceConnected { Serial = "A" });
        bus.Publish(new DeviceConnected { Serial = "B" });     // other deck
        bus.Publish(new PageChanged("main") { Serial = "A" }); // other type
        bus.Publish(new BrokerOnline());                        // service-wide: no serial to filter on

        var events = Drain(subscription);
        Assert.Equal(["DeviceConnected", "BrokerOnline"], events.Select(e => e.Type));
        Assert.Equal("A", events[0].Serial);
    }

    [Fact]
    public void Filter_UnknownType_IsAnError()
    {
        EventFilter.Parse("DeviceConnected,Bogus", null, out var error);
        Assert.Contains("Unknown event type 'Bogus'", error);
    }

    [Fact]
    public void Filter_Empty_MatchesEverything()
    {
        var filter = EventFilter.Parse("", "", out var error);
        Assert.Null(error);
        Assert.Null(filter.Types);
        Assert.Null(filter.Serial);
    }

    // ── Slow subscribers ────────────────────────────────────────────────────

    [Fact]
    public void SlowSubscriber_NeverBlocksThePublisher_AndIsToldToResync()
    {
        var bus = Bus(capacity: 16);
        using var slow = bus.Subscribe();     // never reads
        using var fast = bus.Subscribe();
        var received = 0;

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            bus.Publish(new PageChanged($"p{i}") { Serial = "A" });
            received += Drain(fast).Count;
        }
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"publishing took {watch.Elapsed}");
        Assert.Equal(1000, received);
        Assert.True(slow.TakeOverflow());
        Assert.False(slow.TakeOverflow());    // once, until it overflows again
        Assert.False(fast.TakeOverflow());
        Assert.Equal(16, Drain(slow).Count);  // the oldest ones it had room for
    }

    // ── Coalescing ──────────────────────────────────────────────────────────

    [Fact]
    public void KeyRendered_IsCoalescedPerKey_LatestWins()
    {
        var bus = Bus(maxPerSecond: 4);   // 250 ms per key
        using var subscription = bus.Subscribe();

        bus.Publish(new KeyRendered(0, "h1") { Serial = "A" });   // quiet key: out at once
        bus.Publish(new KeyRendered(0, "h2") { Serial = "A" });
        bus.Publish(new KeyRendered(0, "h3") { Serial = "A" });
        bus.Publish(new KeyRendered(1, "x1") { Serial = "A" });   // another key: not held back

        Assert.Equal(["h1", "x1"], Drain(subscription).Cast<KeyRendered>().Select(k => k.Hash));

        _time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Empty(Drain(subscription));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["h3"], Drain(subscription).Cast<KeyRendered>().Select(k => k.Hash));

        // Quiet again for a full interval: the next one goes out at once.
        _time.Advance(TimeSpan.FromSeconds(1));
        bus.Publish(new KeyRendered(0, "h4") { Serial = "A" });
        Assert.Equal(["h4"], Drain(subscription).Cast<KeyRendered>().Select(k => k.Hash));
    }

    [Fact]
    public void ValueReceived_IsCoalescedPerTopic_AtMostNPerSecond()
    {
        var bus = Bus(maxPerSecond: 2);   // 500 ms per topic
        using var subscription = bus.Subscribe();

        // A chatty topic: 100 messages a second for two seconds.
        var delivered = new List<LiveEvent>();
        for (var i = 0; i < 200; i++)
        {
            bus.Publish(new ValueReceived("sensors/co2", i.ToString()));
            _time.Advance(TimeSpan.FromMilliseconds(10));
            delivered.AddRange(Drain(subscription));
        }
        _time.Advance(TimeSpan.FromSeconds(1));
        delivered.AddRange(Drain(subscription));

        Assert.InRange(delivered.Count, 4, 5);
        Assert.Equal("199", ((ValueReceived)delivered.Last()).Value);   // the latest value always arrives
    }

    [Fact]
    public void IdleCoalescingSlots_ArePruned_PendingOnesKept()
    {
        var bus = Bus(maxPerSecond: 4);
        using var subscription = bus.Subscribe();
        for (var key = 0; key < 100; key++)
            bus.Publish(new ValueReceived($"topic/{key}", "1"));
        Assert.Equal(100, bus.CoalesceSlotCount);

        _time.Advance(EventBus.PruneInterval);
        bus.Publish(new ValueReceived("busy", "1"));
        bus.Publish(new ValueReceived("busy", "2"));   // held back: its slot has a pending event
        Assert.Equal(1, bus.CoalesceSlotCount);

        _time.Advance(TimeSpan.FromSeconds(1));         // "busy" flushes
        _time.Advance(EventBus.PruneInterval);
        bus.Publish(new ValueReceived("other", "1"));
        Assert.Equal(1, bus.CoalesceSlotCount);        // "busy" flushed and went idle, then pruned
        Assert.Equal("2", Drain(subscription).OfType<ValueReceived>().Last(v => v.Topic == "busy").Value);
    }

    [Fact]
    public void UncoalescedEvents_AreNeverHeldBack()
    {
        var bus = Bus(maxPerSecond: 1);
        using var subscription = bus.Subscribe();
        for (var i = 0; i < 10; i++) bus.Publish(new DeviceConnected { Serial = "A" });
        Assert.Equal(10, Drain(subscription).Count);
    }

    // ── Replay ──────────────────────────────────────────────────────────────

    [Fact]
    public void Replay_ReturnsTheMissedEvents_WhileBuffered()
    {
        var bus = Bus(replay: 4);
        for (var i = 1; i <= 6; i++) bus.Publish(new PageChanged($"p{i}") { Serial = i % 2 == 0 ? "A" : "B" });

        Assert.True(bus.TryReplay(4, EventFilter.All, out var all, out var upTo));
        Assert.Equal([5L, 6L], all.Select(e => e.Sequence));
        Assert.Equal(6, upTo);

        Assert.True(bus.TryReplay(4, new EventFilter(Serial: "B"), out var onlyB, out upTo));
        Assert.Equal([5L], onlyB.Select(e => e.Sequence));
        Assert.Equal(6, upTo);   // up to date even though event 6 didn't match

        Assert.True(bus.TryReplay(6, EventFilter.All, out var none, out _));
        Assert.Empty(none);
    }

    [Fact]
    public void Replay_RefusesGapsAndForeignSequences()
    {
        var bus = Bus(replay: 4);
        for (var i = 0; i < 10; i++) bus.Publish(new BrokerOnline());

        Assert.False(bus.TryReplay(2, EventFilter.All, out _, out _));    // 3..6 fell out of the buffer
        Assert.True(bus.TryReplay(6, EventFilter.All, out var events, out _));
        Assert.Equal(4, events.Count);
        Assert.False(bus.TryReplay(11, EventFilter.All, out _, out _));   // from a later (other) run
    }

    [Fact]
    public void Replay_Disabled_StillAcceptsAnUpToDateClient()
    {
        var bus = Bus(replay: 0);
        bus.Publish(new BrokerOnline());
        Assert.True(bus.TryReplay(1, EventFilter.All, out _, out _));
        Assert.False(bus.TryReplay(0, EventFilter.All, out _, out _));
    }

    // ── Disposal ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposedSubscription_Completes_AndGetsNothingMore()
    {
        var bus = Bus();
        var subscription = bus.Subscribe();
        bus.Publish(new BrokerOnline());
        subscription.Dispose();
        bus.Publish(new BrokerOffline(false));

        var events = new List<LiveEvent>();
        await foreach (var e in subscription.Reader.ReadAllAsync()) events.Add(e);   // completes
        Assert.Equal(["BrokerOnline"], events.Select(e => e.Type));
    }

    [Fact]
    public async Task DisposedBus_CompletesSubscribers_AndDropsPendingCoalescedEvents()
    {
        var bus = Bus();
        var subscription = bus.Subscribe();
        bus.Publish(new KeyRendered(0, "h1") { Serial = "A" });
        bus.Publish(new KeyRendered(0, "h2") { Serial = "A" });   // pending

        bus.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));
        bus.Publish(new BrokerOnline());

        var events = new List<LiveEvent>();
        await foreach (var e in subscription.Reader.ReadAllAsync()) events.Add(e);
        Assert.Equal(["h1"], events.Cast<KeyRendered>().Select(k => k.Hash));
    }
}
