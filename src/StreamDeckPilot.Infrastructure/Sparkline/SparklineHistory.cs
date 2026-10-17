using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Core.Rules;

namespace StreamDeckPilot.Infrastructure.Sparkline;

/// <summary>
/// In-memory value history for buttons that opt in to a sparkline (#25). One bounded buffer per
/// (serial, button); nothing is persisted, so a trend starts empty after a restart. Only numeric
/// values are recorded; a text value leaves the trend as it is.
/// </summary>
public sealed class SparklineHistory(IOptions<SparklineOptions> options, TimeProvider? timeProvider = null)
{
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
            return Snapshot(buffer, now, window);
        }
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
