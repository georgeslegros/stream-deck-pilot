using System.Collections.Concurrent;

namespace StreamDeckPilot.Infrastructure.Staleness;

public sealed class LastUpdatedStore
{
    private readonly ConcurrentDictionary<(string Serial, string PageId, int KeyIndex), DateTime> _store = new();

    public void RecordUpdate(string serial, string pageId, int keyIndex) =>
        _store[(serial, pageId, keyIndex)] = DateTime.UtcNow;

    public DateTime? GetLastUpdated(string serial, string pageId, int keyIndex) =>
        _store.TryGetValue((serial, pageId, keyIndex), out var t) ? t : null;
}
