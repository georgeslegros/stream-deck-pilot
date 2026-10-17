using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Core.Rules;

namespace StreamDeckPilot.Infrastructure.Sparkline;

/// <summary>
/// In-memory value history for buttons that opt in to a sparkline (#25). One bounded buffer per
/// (serial, button); nothing is persisted, so a trend starts empty after a restart. Only numeric
/// values are recorded; a text value leaves the trend as it is. Each bucket keeps its latest
/// value, so a spike shorter than a bucket can disappear from the trend.
/// </summary>
public sealed class SparklineHistory(IOptions<SparklineOptions> options, TimeProvider? timeProvider = null)
{
    /// <summary>Shortest gap between two time-only refreshes of a trend.</summary>
    public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(60);

    private readonly int _maxPoints = Math.Max(2, options.Value.MaxPoints);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string Serial, string ButtonId), SparklineBuffer> _buffers = new();

    /// <summary>
    /// Records <paramref name="value"/> (when numeric) for a button that has a sparkline and
    /// returns its trend as of now; null when the button has no sparkline or no numeric value yet.
    /// </summary>
    public SparklineSeries? Record(string serial, ButtonDefinition button, string? value)
    {
        if (button.Display?.Sparkline is not { Hours: > 0 } spec) return null;

        var now = _time.GetUtcNow();
        var window = TimeSpan.FromHours(Math.Min(spec.Hours, SparklineSpec.MaxHours));
        // +1: the one point older than the window that gives the value at its left edge.
        var buffer = _buffers.GetOrAdd((serial, button.ButtonId), _ => new SparklineBuffer(_maxPoints + 1));

        lock (buffer)
        {
            if (value is not null && RuleCondition.TryParseNumber(value, out var number) && double.IsFinite(number))
            {
                // A value in the same bucket as the previous one replaces it, so the buffer spans
                // the whole window however often the topic publishes.
                var bucket = window / _maxPoints;
                if (buffer.Count > 0 && now - buffer[buffer.Count - 1].At < bucket)
                    buffer.ReplaceLastValue(number);
                else
                    buffer.Add(now, number);
            }
            buffer.LastSnapshot = now;
            return Snapshot(buffer, now, window);
        }
    }

    /// <summary>
    /// The trend moved on in time with no new value: returns it as of now when it was last drawn
    /// at least one bucket ago (and at least <see cref="MinRefreshInterval"/>), else null. Lets a
    /// topic that only publishes on change slide its window instead of freezing, then jumping.
    /// </summary>
    public SparklineSeries? Refresh(string serial, ButtonDefinition button)
    {
        if (button.Display?.Sparkline is not { Hours: > 0 } spec) return null;
        if (!_buffers.TryGetValue((serial, button.ButtonId), out var buffer)) return null;

        var now = _time.GetUtcNow();
        var window = TimeSpan.FromHours(Math.Min(spec.Hours, SparklineSpec.MaxHours));
        var interval = window / _maxPoints;
        if (interval < MinRefreshInterval) interval = MinRefreshInterval;

        lock (buffer)
        {
            if (buffer.Count == 0 || now - buffer.LastSnapshot < interval) return null;
            buffer.LastSnapshot = now;
            return Snapshot(buffer, now, window);
        }
    }

    /// <summary>Forgets the trends of buttons a device's config no longer has (removed or renamed).</summary>
    public void Retain(string serial, IReadOnlySet<string> buttonIds)
    {
        foreach (var key in _buffers.Keys)
            if (key.Serial == serial && !buttonIds.Contains(key.ButtonId))
                _buffers.TryRemove(key, out _);
    }

    internal static SparklineSeries? Snapshot(SparklineBuffer buffer, DateTimeOffset now, TimeSpan window)
    {
        var start = now - window;
        // Keep at most one point at or before the window start: it gives the left-edge value.
        while (buffer.Count >= 2 && buffer[1].At <= start)
            buffer.RemoveOldest();
        if (buffer.Count == 0) return null;

        var points = new List<SparklinePoint>(buffer.Count + 1);
        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < buffer.Count; i++)
        {
            var (at, v) = buffer[i];
            points.Add(new SparklinePoint(Math.Clamp((at - start) / window, 0, 1), v));
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        // Carry the last value forward to now: a topic that only publishes on change still draws
        // a line up to the right edge.
        if (points[^1].X < 1)
            points.Add(new SparklinePoint(1, points[^1].Value));
        return new SparklineSeries(points, min, max);
    }
}
