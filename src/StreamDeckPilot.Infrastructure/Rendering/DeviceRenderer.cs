using System.Collections.Concurrent;
using OpenMacroBoard.SDK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Observability;

namespace StreamDeckPilot.Infrastructure.Rendering;

public sealed class DeviceRenderer : IDeviceRenderer
{
    private readonly KeyBitmapComposer? _composer;
    private readonly StreamDeckMetrics? _metrics;
    private readonly ConcurrentDictionary<(string Serial, int KeyIndex), object> _keyLocks = new();

    private readonly BrokerIndicator? _broker;
    private readonly BlinkScheduler? _blink;
    private readonly PressFeedbackTracker? _feedback;

    public DeviceRenderer(BrokerIndicator? broker = null, BlinkScheduler? blink = null, PressFeedbackTracker? feedback = null)
    {
        _broker = broker;
        _blink = blink;
        _feedback = feedback;
    }

    public DeviceRenderer(KeyBitmapComposer composer, StreamDeckMetrics? metrics = null, BrokerIndicator? broker = null,
        BlinkScheduler? blink = null, PressFeedbackTracker? feedback = null)
    {
        _composer = composer;
        _metrics = metrics;
        _broker = broker;
        _blink = blink;
        _feedback = feedback;
    }

    public void RenderButton(IMacroBoard board, string serial, int keyIndex, ButtonRenderState state)
    {
        if (!board.IsConnected) return;

        // Broker-offline overlay (#26), applied to a copy at paint time so desired state keeps the
        // real value. Painting reads the flag here, under the per-key lock when called through
        // RenderLatest/RenderAll, and every flip of the flag is followed by a repaint.
        // Paint-time overlays, in precedence order: offline (#26) > press feedback (#22) > blink
        // phase (#23).
        if (state.DependsOnBroker && _broker?.ShowOffline == true)
            state = state with { IsOffline = true };
        // Press feedback: the offline look wins, so no feedback then.
        else if (_feedback?.Get(serial, state.ButtonId) is { } feedback and not PressFeedback.None)
            state = state with { Feedback = feedback };

        // Blink: the offline look wins (a blinking alert can't be trusted while its value has
        // stopped updating, so it shows offline and stops blinking). Press feedback wins too: a
        // blinking key that was just pressed shows its full "on" look under the ring or red wash,
        // never the dimmed off phase, so the answer to the press is always readable.
        var blinkFrame = false;
        if (state.BlinkActive && _blink is not null)
        {
            // Always asked, so the scheduler knows which frame this key now shows (#131).
            var level = _blink.PaintLevel(serial, keyIndex, state.Blink!);
            if (state.Feedback == PressFeedback.None)
            {
                state = state with { BlinkLevel = level };
                blinkFrame = true;
            }
        }
        if (!blinkFrame) DropBlinkFrames(serial, keyIndex);

        try
        {
            var bitmap = _composer is null ? FallbackBitmap(state)
                : blinkFrame ? BlinkFrame(serial, keyIndex, state)
                : _composer.Compose(state, serial);
            board.SetKeyBitmap(keyIndex, bitmap);
            _metrics?.RenderOperations.Add(1, [new("serial", serial)]);
        }
        catch
        {
            _metrics?.RenderFailures.Add(1, [new("serial", serial)]);
            throw;
        }
    }

    public void RenderLatest(IMacroBoard board, string serial, string pageId, int keyIndex, DesiredStateStore stateStore)
    {
        lock (_keyLocks.GetOrAdd((serial, keyIndex), _ => new object()))
        {
            var state = stateStore.Get(serial, pageId, keyIndex);
            if (state is not null) RenderButton(board, serial, keyIndex, state);
        }
    }

    public void RenderAll(IMacroBoard board, string serial, string pageId, DesiredStateStore stateStore)
    {
        if (!board.IsConnected) return;

        var bound = new HashSet<int>();
        foreach (var (keyIndex, _) in stateStore.GetPage(serial, pageId))
        {
            RenderLatest(board, serial, pageId, keyIndex, stateStore);
            bound.Add(keyIndex);
        }

        // Blank every key with no button on this page. Without this, a key that
        // showed an icon/value on a previously active page keeps its stale bitmap
        // after navigation (the old page is never iterated again). An unconfigured
        // key is true black (see key-rendering-redesign.md, Layout D).
        for (var keyIndex = 0; keyIndex < board.Keys.Count; keyIndex++)
        {
            if (bound.Contains(keyIndex)) continue;
            ClearKey(board, serial, keyIndex);
        }
    }

    private void ClearKey(IMacroBoard board, string serial, int keyIndex)
    {
        try
        {
            board.SetKeyBitmap(keyIndex, KeyBitmap.Black);
            _metrics?.RenderOperations.Add(1, [new("serial", serial)]);
        }
        catch
        {
            _metrics?.RenderFailures.Add(1, [new("serial", serial)]);
            throw;
        }
    }

    // Blink frames (#131): a pulse repaints a key up to 12 times a cycle, so the key is composed once
    // at full brightness and every frame is derived from that image by dimming it, then kept per
    // level. A new state for the key (value, colour, sparkline…) replaces the whole entry.
    private sealed class BlinkFrames(ButtonRenderState baseState, Image<Rgba32> full)
    {
        public ButtonRenderState BaseState { get; } = baseState;
        public Image<Rgba32> Full { get; } = full;
        public Dictionary<float, KeyBitmap> ByLevel { get; } = new();
    }

    // Guarded by the key's entry in _keyLocks (re-entrant, and already held via RenderLatest).
    private readonly ConcurrentDictionary<(string Serial, int KeyIndex), BlinkFrames> _blinkFrames = new();

    /// <summary>Full compositions done for blink frames; tests use it to check the frame cache.</summary>
    internal int BlinkFrameCompositions;

    private KeyBitmap BlinkFrame(string serial, int keyIndex, ButtonRenderState state)
    {
        lock (KeyLock(serial, keyIndex))
        {
            var baseState = state with { BlinkLevel = 1f };
            if (!_blinkFrames.TryGetValue((serial, keyIndex), out var frames) || frames.BaseState != baseState)
            {
                frames?.Full.Dispose();
                Interlocked.Increment(ref BlinkFrameCompositions);
                frames = new BlinkFrames(baseState, _composer!.ComposeImage(baseState, serial));
                _blinkFrames[(serial, keyIndex)] = frames;
            }

            if (frames.ByLevel.TryGetValue(state.BlinkLevel, out var cached)) return cached;
            KeyBitmap bitmap;
            if (state.BlinkLevel >= 1f)
                bitmap = KeyBitmap.Create.FromImageSharpImage(frames.Full);
            else
            {
                using var dimmed = frames.Full.Clone();
                KeyBitmapComposer.Darken(dimmed, state.BlinkLevel);
                bitmap = KeyBitmap.Create.FromImageSharpImage(dimmed);
            }
            frames.ByLevel[state.BlinkLevel] = bitmap;
            return bitmap;
        }
    }

    private void DropBlinkFrames(string serial, int keyIndex)
    {
        if (_blinkFrames.IsEmpty) return;
        lock (KeyLock(serial, keyIndex))
            if (_blinkFrames.TryRemove((serial, keyIndex), out var frames))
                frames.Full.Dispose();
    }

    private object KeyLock(string serial, int keyIndex) => _keyLocks.GetOrAdd((serial, keyIndex), _ => new object());

    // Colour-only fallback used when no composer is injected (tests)
    private static KeyBitmap FallbackBitmap(ButtonRenderState state)
    {
        if (state.IsDimmed || state.IsOffline || state.BlinkLevel < 1f) return KeyBitmap.Create.FromRgb(30, 30, 30);
        if (state.Feedback == PressFeedback.Failed) return KeyBitmap.Create.FromRgb(200, 0, 0);
        if (state.Feedback == PressFeedback.Pending) return KeyBitmap.Create.FromRgb(255, 255, 255);
        return ParseColour(state.BackgroundColour);
    }

    private static KeyBitmap ParseColour(string? hex)
    {
        if (hex is null || hex.Length < 7 || hex[0] != '#') return KeyBitmap.Black;
        try
        {
            var r = Convert.ToByte(hex.Substring(1, 2), 16);
            var g = Convert.ToByte(hex.Substring(3, 2), 16);
            var b = Convert.ToByte(hex.Substring(5, 2), 16);
            return KeyBitmap.Create.FromRgb(r, g, b);
        }
        catch { return KeyBitmap.Black; }
    }
}
