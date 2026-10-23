using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;

namespace StreamDeckPilot.Web.Services;

/// <summary>A rendered image, inlined as a data URI, with the content hash it was made from.</summary>
public sealed record InlineImage(string DataUri, string ETag);

/// <summary>
/// What the dashboard (#99) reads, per circuit. Everything is pushed: the live snapshot and its
/// events (#108), plus a notice whenever a deck's desired state changes (a value arrived, a key
/// went stale), so off-screen pages and staleness update without anything polling.
/// Events are raised off the circuit's thread: components use <c>InvokeAsync</c>.
/// </summary>
public interface IDashboardSource
{
    /// <summary>Starts the live subscription (cheap after the first call) and returns the snapshot.</summary>
    Task<LiveSnapshot> StartAsync();

    /// <summary>The current snapshot; null until <see cref="StartAsync"/> completed.</summary>
    LiveSnapshot? Snapshot { get; }

    /// <summary>The snapshot changed (any live event).</summary>
    event Action? Changed;

    /// <summary>Each live event after it was applied to <see cref="Snapshot"/>.</summary>
    event Action<LiveEvent>? EventApplied;

    /// <summary>A deck's desired state was written (serial). Bursty: callers coalesce.</summary>
    event Action<string>? DesiredStateChanged;

    Task<DeviceConfig?> GetConfigAsync(string serial);

    /// <summary>One key of the page on screen, as the deck shows it; null for an unknown deck or key.</summary>
    Task<InlineImage?> GetKeyImageAsync(string serial, int keyIndex);

    /// <summary>A whole page rendered from desired state; null for an unknown deck or page.</summary>
    Task<InlineImage?> GetPageImageAsync(string serial, string pageId);

    /// <summary>
    /// Whether the renderer currently shows this key dimmed: the staleness monitor's verdict (or a
    /// setpoint's), read from desired state rather than re-derived here.
    /// </summary>
    bool IsDimmed(string serial, string pageId, int keyIndex);
}

/// <summary>
/// Per circuit. Images are rendered in-process by <see cref="DevicePreviewService"/> and sent over
/// the circuit as data URIs: the browser never fetches an image URL, so no image endpoint has to be
/// opened without the API key, and the circuit's sign-in (#97) covers the images.
/// </summary>
public sealed class DashboardSource : IDashboardSource, IDisposable
{
    private readonly LiveState _live;
    private readonly ConfigStore _configs;
    private readonly DevicePreviewService _previews;
    private readonly DesiredStateStore _desired;

    public DashboardSource(LiveState live, ConfigStore configs, DevicePreviewService previews, DesiredStateStore desired)
    {
        _live = live;
        _configs = configs;
        _previews = previews;
        _desired = desired;
        _desired.Changed += OnDesiredChanged;
    }

    public LiveSnapshot? Snapshot => _live.Snapshot;

    public event Action? Changed
    {
        add => _live.Changed += value;
        remove => _live.Changed -= value;
    }

    public event Action<LiveEvent>? EventApplied
    {
        add => _live.EventApplied += value;
        remove => _live.EventApplied -= value;
    }

    public event Action<string>? DesiredStateChanged;

    public async Task<LiveSnapshot> StartAsync()
    {
        await _live.StartAsync();
        return _live.Snapshot!;
    }

    public Task<DeviceConfig?> GetConfigAsync(string serial) => _configs.LoadAsync(serial);

    public async Task<InlineImage?> GetKeyImageAsync(string serial, int keyIndex) =>
        Inline(await _previews.RenderKeyAsync(serial, keyIndex));

    public async Task<InlineImage?> GetPageImageAsync(string serial, string pageId) =>
        Inline(await _previews.RenderBoardAsync(serial, pageId));

    public bool IsDimmed(string serial, string pageId, int keyIndex) =>
        _desired.Get(serial, pageId, keyIndex)?.IsDimmed == true;

    private static InlineImage? Inline(PreviewResult result) =>
        result is { Status: PreviewStatus.Ok, Image: { } image }
            ? new InlineImage("data:image/png;base64," + Convert.ToBase64String(image.Png), image.ETag)
            : null;

    private void OnDesiredChanged(string serial) => DesiredStateChanged?.Invoke(serial);

    public void Dispose() => _desired.Changed -= OnDesiredChanged;
}
