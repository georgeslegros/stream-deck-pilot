namespace StreamDeckPilot.Tests.Brightness;

/// <summary>TimeProvider whose timers only fire when the test calls <see cref="FireAll"/>.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public IReadOnlyList<ManualTimer> Timers => _timers;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime);
        _timers.Add(timer);
        return timer;
    }

    // Fires every armed timer once, as if its due time had elapsed.
    public void FireAll()
    {
        foreach (var t in _timers.ToList()) t.FireIfArmed();
    }

    public sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan? DueTime { get; private set; } = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
            return true;
        }

        internal void FireIfArmed()
        {
            if (DueTime is null) return;
            DueTime = null;
            callback(state);
        }

        public void Dispose() => DueTime = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
