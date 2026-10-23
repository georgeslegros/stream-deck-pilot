using System.Collections.Immutable;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Web.Services;

namespace StreamDeckPilot.Tests.Web;

/// <summary>
/// The dashboard's data for bUnit tests (#99): a snapshot the test controls, configs, key images
/// that change when the test says so, and pushes raised off the renderer's thread like the real bus.
/// </summary>
public sealed class FakeDashboardSource : IDashboardSource
{
    private Action? _changed;
    private Action<LiveEvent>? _eventApplied;
    private Action<string>? _desiredStateChanged;

    public LiveSnapshot? Snapshot { get; set; } = Snap();
    public Dictionary<string, DeviceConfig> Configs { get; } = new();
    public HashSet<(string Serial, string PageId, int Key)> Dimmed { get; } = new();

    /// <summary>Image version per (serial, key): bump it to make that key's image change.</summary>
    public Dictionary<(string Serial, int Key), int> KeyVersions { get; } = new();
    public List<(string Serial, int Key)> KeyImageRequests { get; } = new();
    public List<(string Serial, string PageId)> PageImageRequests { get; } = new();

    public int Listeners =>
        (_changed?.GetInvocationList().Length ?? 0) +
        (_eventApplied?.GetInvocationList().Length ?? 0) +
        (_desiredStateChanged?.GetInvocationList().Length ?? 0);

    public event Action? Changed { add => _changed += value; remove => _changed -= value; }
    public event Action<LiveEvent>? EventApplied { add => _eventApplied += value; remove => _eventApplied -= value; }
    public event Action<string>? DesiredStateChanged { add => _desiredStateChanged += value; remove => _desiredStateChanged -= value; }

    public Task<LiveSnapshot> StartAsync() => Task.FromResult(Snapshot!);

    public Task<DeviceConfig?> GetConfigAsync(string serial) =>
        Task.FromResult(Configs.TryGetValue(serial, out var config) ? config : null);

    public Task<InlineImage?> GetKeyImageAsync(string serial, int keyIndex)
    {
        lock (KeyImageRequests) KeyImageRequests.Add((serial, keyIndex));
        if (Snapshot?.Device(serial) is null) return Task.FromResult<InlineImage?>(null);
        var version = KeyVersions.GetValueOrDefault((serial, keyIndex));
        return Task.FromResult<InlineImage?>(new($"data:image/png;base64,{serial}-{keyIndex}-{version}", $"{serial}-{keyIndex}-v{version}"));
    }

    public Task<InlineImage?> GetPageImageAsync(string serial, string pageId)
    {
        lock (PageImageRequests) PageImageRequests.Add((serial, pageId));
        return Task.FromResult<InlineImage?>(new($"data:image/png;base64,{serial}-{pageId}", $"{serial}-{pageId}"));
    }

    public bool IsDimmed(string serial, string pageId, int keyIndex) => Dimmed.Contains((serial, pageId, keyIndex));

    /// <summary>What the live view does with a bus event: apply it, then raise the events off the renderer's thread.</summary>
    public void Push(LiveEvent e)
    {
        Snapshot = Snapshot!.Apply(e) ?? Snapshot;
        Task.Run(() =>
        {
            _eventApplied?.Invoke(e);
            _changed?.Invoke();
        }).Wait();
    }

    /// <summary>A desired-state write for <paramref name="serial"/> (a value arrived, a key went stale).</summary>
    public void PushDesiredState(string serial) => Task.Run(() => _desiredStateChanged?.Invoke(serial)).Wait();

    public static DeviceSnapshot Deck(string serial, DeviceConnectionState state = DeviceConnectionState.Connected,
        string? activePage = "main") =>
        new(serial, "Stream Deck MK.2", 3, 5, state, activePage, ImmutableDictionary<int, string>.Empty);

    public static LiveSnapshot Snap(params DeviceSnapshot[] devices) => new(
        "boot", 0, new BrokerSnapshot(true, false, null), 80, [.. devices],
        ImmutableDictionary<string, TopicValue>.Empty);
}
