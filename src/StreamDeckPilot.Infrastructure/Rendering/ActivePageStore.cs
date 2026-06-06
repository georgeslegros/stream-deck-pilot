using System.Collections.Concurrent;

namespace StreamDeckPilot.Infrastructure.Rendering;

public sealed class ActivePageStore
{
    private readonly ConcurrentDictionary<string, string> _pages = new();

    public string? GetActivePage(string serial) =>
        _pages.TryGetValue(serial, out var p) ? p : null;

    public void SetActivePage(string serial, string pageId) =>
        _pages[serial] = pageId;

    public void Clear(string serial) =>
        _pages.TryRemove(serial, out _);
}
