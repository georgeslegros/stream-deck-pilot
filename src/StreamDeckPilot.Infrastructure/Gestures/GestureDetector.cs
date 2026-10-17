using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Infrastructure.Gestures;

/// <summary>A recognised gesture: run the actions <paramref name="Gesture"/> maps to on that key of that page.</summary>
public readonly record struct GestureEvent(int KeyIndex, string PageId, string Gesture);

/// <summary>
/// Turns one device's key-down/key-up events into gestures. Not thread-safe: it is driven by that
/// device's single press consumer, and its timers only call <c>onTimer</c>, which must route the
/// tick back through the same consumer (<see cref="TimerElapsed"/>) so every state change stays in
/// press order.
/// <list type="bullet">
/// <item>A key that defines neither <c>LongPress</c> nor <c>DoubleTap</c> gives its <c>Tap</c> on
/// key-down, with no waiting, exactly as before gestures existed.</item>
/// <item><c>LongPress</c> fires once the key has been held for <see cref="GestureOptions.LongPressMs"/>,
/// while it is still down; the release then does nothing. A shorter hold is a tap, given on release.</item>
/// <item>With <c>DoubleTap</c>, a release waits <see cref="GestureOptions.DoubleTapMs"/> for a second
/// press (which gives <c>DoubleTap</c> at once) before giving <c>Tap</c>. A press on any other key ends
/// the wait early, so taps still run in the order they were pressed.</item>
/// </list>
/// </summary>
public sealed class GestureDetector(GestureOptions options, TimeProvider time, Action<int, long> onTimer) : IDisposable
{
    private sealed class KeyState(string pageId, bool longPress, bool doubleTap)
    {
        public string PageId { get; } = pageId;
        public bool LongPress { get; } = longPress;
        public bool DoubleTap { get; } = doubleTap;
        public bool Held { get; set; } = true;
        public bool Consumed { get; set; }        // LongPress/DoubleTap already given: the release is silent
        public bool AwaitingSecondTap { get; set; }
        public long Generation { get; set; }       // identifies the armed timer; stale ticks don't match
        public ITimer? Timer { get; set; }
    }

    private readonly Dictionary<int, KeyState> _keys = new();
    private long _generation;

    /// <param name="definedGestures">The gesture names the pressed key's button defines (empty if none).</param>
    public IReadOnlyList<GestureEvent> KeyDown(int keyIndex, string pageId, IEnumerable<string> definedGestures)
    {
        var events = new List<GestureEvent>();

        if (_keys.TryGetValue(keyIndex, out var state))
        {
            if (state.AwaitingSecondTap && state.PageId == pageId)
            {
                FlushPendingTaps(events, except: keyIndex);
                Disarm(state);
                state.AwaitingSecondTap = false;
                state.Held = true;
                state.Consumed = true;
                events.Add(new(keyIndex, pageId, GestureNames.DoubleTap));
                return events;
            }
            // A first tap left on another page resolves now; a hold whose release was lost is dropped.
            if (state.AwaitingSecondTap) events.Add(new(keyIndex, state.PageId, GestureNames.Tap));
            Disarm(state);
            _keys.Remove(keyIndex);
        }

        FlushPendingTaps(events, except: keyIndex);

        var defined = definedGestures.ToHashSet();
        var longPress = defined.Contains(GestureNames.LongPress);
        var doubleTap = defined.Contains(GestureNames.DoubleTap);
        if (!longPress && !doubleTap)
        {
            events.Add(new(keyIndex, pageId, GestureNames.Tap));
            return events;
        }

        state = new KeyState(pageId, longPress, doubleTap);
        _keys[keyIndex] = state;
        if (longPress) Arm(keyIndex, state, options.LongPressMs);
        return events;
    }

    public IReadOnlyList<GestureEvent> KeyUp(int keyIndex)
    {
        // No state: a key that already gave its Tap on key-down, or a press that only woke a dark deck.
        if (!_keys.TryGetValue(keyIndex, out var state) || !state.Held) return [];

        state.Held = false;
        Disarm(state);
        if (state.Consumed)
        {
            _keys.Remove(keyIndex);
            return [];
        }
        if (state.DoubleTap)
        {
            state.AwaitingSecondTap = true;
            Arm(keyIndex, state, options.DoubleTapMs);
            return [];
        }
        _keys.Remove(keyIndex);
        return [new(keyIndex, state.PageId, GestureNames.Tap)];
    }

    public IReadOnlyList<GestureEvent> TimerElapsed(int keyIndex, long generation)
    {
        if (!_keys.TryGetValue(keyIndex, out var state) || state.Generation != generation) return [];

        Disarm(state);
        if (state.Held && !state.Consumed && state.LongPress)
        {
            state.Consumed = true;
            return [new(keyIndex, state.PageId, GestureNames.LongPress)];
        }
        if (state.AwaitingSecondTap)
        {
            _keys.Remove(keyIndex);
            return [new(keyIndex, state.PageId, GestureNames.Tap)];
        }
        return [];
    }

    private void FlushPendingTaps(List<GestureEvent> events, int except)
    {
        foreach (var (key, state) in _keys.Where(k => k.Key != except && k.Value.AwaitingSecondTap).ToList())
        {
            Disarm(state);
            _keys.Remove(key);
            events.Add(new(key, state.PageId, GestureNames.Tap));
        }
    }

    private void Arm(int keyIndex, KeyState state, int milliseconds)
    {
        var generation = state.Generation = ++_generation;
        state.Timer = time.CreateTimer(_ => onTimer(keyIndex, generation), null,
            TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)), Timeout.InfiniteTimeSpan);
    }

    // Also bumps the generation, so a tick already dispatched by the old timer is ignored.
    private void Disarm(KeyState state)
    {
        state.Timer?.Dispose();
        state.Timer = null;
        state.Generation = ++_generation;
    }

    public void Dispose()
    {
        foreach (var state in _keys.Values) state.Timer?.Dispose();
        _keys.Clear();
    }
}
