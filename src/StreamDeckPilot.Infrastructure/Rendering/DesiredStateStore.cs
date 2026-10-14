using System.Collections.Concurrent;
using StreamDeckPilot.Core.Rendering;

namespace StreamDeckPilot.Infrastructure.Rendering;

public sealed class DesiredStateStore
{
    private readonly ConcurrentDictionary<(string Serial, string PageId, int KeyIndex), ButtonRenderState> _store = new();

    // Serialises writes so TryUpdate's check-then-set can't interleave with a Set. Reads stay lock-free.
    private readonly object _writeLock = new();

    public void Set(string serial, string pageId, int keyIndex, ButtonRenderState state)
    {
        lock (_writeLock) _store[(serial, pageId, keyIndex)] = state;
    }

    /// <summary>
    /// Compare-and-set: stores <paramref name="next"/> only if the current state is still the exact
    /// instance <paramref name="expected"/> (reference equality, so a newer state that happens to look
    /// the same still counts as changed). Returns false when another writer got there first.
    /// </summary>
    public bool TryUpdate(string serial, string pageId, int keyIndex, ButtonRenderState expected, ButtonRenderState next)
    {
        lock (_writeLock)
        {
            if (!_store.TryGetValue((serial, pageId, keyIndex), out var current) || !ReferenceEquals(current, expected))
                return false;
            _store[(serial, pageId, keyIndex)] = next;
            return true;
        }
    }

    public ButtonRenderState? Get(string serial, string pageId, int keyIndex) =>
        _store.TryGetValue((serial, pageId, keyIndex), out var s) ? s : null;

    public IReadOnlyList<(int KeyIndex, ButtonRenderState State)> GetPage(string serial, string pageId) =>
        _store
            .Where(kv => kv.Key.Serial == serial && kv.Key.PageId == pageId)
            .Select(kv => (kv.Key.KeyIndex, kv.Value))
            .ToList();

    public void Clear(string serial)
    {
        lock (_writeLock)
        {
            foreach (var key in _store.Keys.Where(k => k.Serial == serial).ToList())
                _store.TryRemove(key, out _);
        }
    }
}
