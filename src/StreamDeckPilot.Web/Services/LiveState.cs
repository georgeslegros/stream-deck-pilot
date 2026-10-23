using StreamDeckPilot.Infrastructure.Events;

namespace StreamDeckPilot.Web.Services;

/// <summary>
/// The live state for one browser circuit (#108): a <see cref="LiveView"/> started on first use and
/// disposed with the circuit. Components read <see cref="Snapshot"/> and re-render on
/// <see cref="Changed"/> (raised off the circuit's thread: use <c>InvokeAsync</c>). Updates are
/// pushed by the event bus; nothing in the UI polls. After a reconnect to the same circuit the
/// view is still current; a new circuit (server restart, page reload) starts from a new snapshot.
/// </summary>
public sealed class LiveState(LiveView view)
{
    public LiveSnapshot? Snapshot => view.Current;

    public event Action? Changed
    {
        add => view.Changed += value;
        remove => view.Changed -= value;
    }

    /// <summary>Raised with each applied event, for views that react to the event itself (e.g. a key image).</summary>
    public event Action<LiveEvent>? EventApplied
    {
        add => view.EventApplied += value;
        remove => view.EventApplied -= value;
    }

    /// <summary>Subscribes and loads the snapshot; cheap after the first call.</summary>
    public Task StartAsync() => view.StartAsync();
}
