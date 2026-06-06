using System.Collections.Concurrent;
using StreamDeckPilot.Core.Rendering;

namespace StreamDeckPilot.Infrastructure.Rendering;

public sealed class DesiredStateStore
{
    private readonly ConcurrentDictionary<(string Serial, string PageId, int KeyIndex), ButtonRenderState> _store = new();

    public void Set(string serial, string pageId, int keyIndex, ButtonRenderState state) =>
        _store[(serial, pageId, keyIndex)] = state;

    public ButtonRenderState? Get(string serial, string pageId, int keyIndex) =>
        _store.TryGetValue((serial, pageId, keyIndex), out var s) ? s : null;

    public IReadOnlyList<(int KeyIndex, ButtonRenderState State)> GetPage(string serial, string pageId) =>
        _store
            .Where(kv => kv.Key.Serial == serial && kv.Key.PageId == pageId)
            .Select(kv => (kv.Key.KeyIndex, kv.Value))
            .ToList();

    public void Clear(string serial)
    {
        foreach (var key in _store.Keys.Where(k => k.Serial == serial).ToList())
            _store.TryRemove(key, out _);
    }
}
