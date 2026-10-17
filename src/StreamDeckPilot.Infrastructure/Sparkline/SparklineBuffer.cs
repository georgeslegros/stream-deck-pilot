namespace StreamDeckPilot.Infrastructure.Sparkline;

/// <summary>
/// Fixed-capacity ring buffer of timestamped values, oldest first. When full, adding drops the
/// oldest point. Not thread-safe: <see cref="SparklineHistory"/> locks around it.
/// </summary>
internal sealed class SparklineBuffer(int capacity)
{
    private readonly (DateTimeOffset At, double Value)[] _items = new (DateTimeOffset, double)[Math.Max(1, capacity)];
    private int _head;

    public int Count { get; private set; }

    /// <summary>When the trend was last handed out to be drawn (see <see cref="SparklineHistory.Refresh"/>).</summary>
    public DateTimeOffset LastSnapshot { get; set; }
    public int Capacity => _items.Length;

    public (DateTimeOffset At, double Value) this[int index] => _items[(_head + index) % _items.Length];

    public void Add(DateTimeOffset at, double value)
    {
        if (Count == _items.Length)
            RemoveOldest();
        _items[(_head + Count) % _items.Length] = (at, value);
        Count++;
    }

    public void ReplaceLastValue(double value)
    {
        var i = (_head + Count - 1) % _items.Length;
        _items[i] = (_items[i].At, value);
    }

    public void RemoveOldest()
    {
        _head = (_head + 1) % _items.Length;
        Count--;
    }
}
