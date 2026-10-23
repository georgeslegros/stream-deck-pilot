using System.Collections.Concurrent;

namespace StreamDeckPilot.Infrastructure.Rendering;

public sealed class ActivePageStore
{
    private readonly ConcurrentDictionary<string, string> _pages = new();

    /// <summary>Raised with (serial, page id) when a deck's active page changes to a different page.</summary>
    public event Action<string, string>? Changed;

    public string? GetActivePage(string serial) =>
        _pages.TryGetValue(serial, out var p) ? p : null;

    public void SetActivePage(string serial, string pageId)
    {
        var previous = _pages.TryGetValue(serial, out var p) ? p : null;
        _pages[serial] = pageId;
        if (!string.Equals(previous, pageId, StringComparison.Ordinal))
            Changed?.Invoke(serial, pageId);
    }

    public void Clear(string serial) =>
        _pages.TryRemove(serial, out _);
}
