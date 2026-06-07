using OpenMacroBoard.SDK;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Observability;

namespace StreamDeckPilot.Infrastructure.Rendering;

public sealed class DeviceRenderer : IDeviceRenderer
{
    private readonly KeyBitmapComposer? _composer;
    private readonly StreamDeckMetrics? _metrics;

    public DeviceRenderer() { }

    public DeviceRenderer(KeyBitmapComposer composer, StreamDeckMetrics? metrics = null)
    {
        _composer = composer;
        _metrics = metrics;
    }

    public void RenderButton(IMacroBoard board, string serial, int keyIndex, ButtonRenderState state)
    {
        if (!board.IsConnected) return;

        try
        {
            var bitmap = _composer is not null
                ? _composer.Compose(state, serial)
                : FallbackBitmap(state);
            board.SetKeyBitmap(keyIndex, bitmap);
            _metrics?.RenderOperations.Add(1, [new("serial", serial)]);
        }
        catch
        {
            _metrics?.RenderFailures.Add(1, [new("serial", serial)]);
            throw;
        }
    }

    public void RenderAll(IMacroBoard board, string serial, string pageId, DesiredStateStore stateStore)
    {
        if (!board.IsConnected) return;

        var bound = new HashSet<int>();
        foreach (var (keyIndex, state) in stateStore.GetPage(serial, pageId))
        {
            RenderButton(board, serial, keyIndex, state);
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

    // Colour-only fallback used when no composer is injected (tests)
    private static KeyBitmap FallbackBitmap(ButtonRenderState state)
    {
        if (state.IsDimmed) return KeyBitmap.Create.FromRgb(30, 30, 30);
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
