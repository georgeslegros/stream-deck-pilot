using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Sparkline;

namespace StreamDeckPilot.Tests.Sparkline;

public sealed class SparklineHistoryTests
{
    private const string Serial = "SN1";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ButtonDefinition Button(double? hours, string id = "co2") =>
        new(id, 0, "main",
            new DisplaySpec(Sparkline: hours is { } h ? new SparklineSpec(h) : null),
            new InboundBinding("sensors/co2", null, null, false, null),
            [], new Dictionary<string, IReadOnlyList<ButtonAction>>());

    private static (SparklineHistory History, Clock Clock) Create(int maxPoints = 240)
    {
        var clock = new Clock();
        return (new SparklineHistory(Options.Create(new SparklineOptions { MaxPoints = maxPoints }), clock), clock);
    }

    [Fact]
    public void ButtonWithoutSparkline_RecordsNothing()
    {
        var (history, _) = Create();
        Assert.Null(history.Record(Serial, Button(null), "500"));
    }

    [Fact]
    public void SinglePoint_IsCarriedForwardAsAFlatLine()
    {
        var (history, _) = Create();
        var series = history.Record(Serial, Button(6), "500");

        Assert.NotNull(series);
        Assert.All(series.Points, p => Assert.Equal(500, p.Value));
        Assert.Equal(1, series.Points[^1].X);
        Assert.Equal((500, 500), (series.Min, series.Max));
    }

    [Fact]
    public void SparseData_TwoPointsHoursApart_DrawAStep()
    {
        // A topic that only publishes on change: 400 at T0, 900 three hours later.
        var (history, clock) = Create();
        var button = Button(6);
        history.Record(Serial, button, "400");
        clock.Now = T0.AddHours(3);
        var series = history.Record(Serial, button, "900")!;

        // 400 holds from the middle of the 6 h window until the change at the right edge.
        Assert.Equal([new SparklinePoint(0.5, 400), new SparklinePoint(1, 900)], series.Points);
        Assert.Equal((400, 900), (series.Min, series.Max));
    }

    [Fact]
    public void SparseData_NoNewValue_CarriesTheLastValueToNow()
    {
        var (history, clock) = Create();
        var button = Button(6);
        history.Record(Serial, button, "400");
        clock.Now = T0.AddHours(2);
        // A text payload (e.g. "unavailable") is not recorded but still yields the trend as of now.
        var series = history.Record(Serial, button, "unavailable")!;

        Assert.Equal(2, series.Points.Count);
        Assert.Equal(new SparklinePoint(4.0 / 6, 400), series.Points[0]);
        Assert.Equal(new SparklinePoint(1, 400), series.Points[1]);
    }

    [Fact]
    public void PointsOlderThanTheWindow_AreDropped_ButTheLastOneGivesTheLeftEdge()
    {
        var (history, clock) = Create();
        var button = Button(1);
        history.Record(Serial, button, "10");
        clock.Now = T0.AddMinutes(30);
        history.Record(Serial, button, "20");
        clock.Now = T0.AddMinutes(90);
        history.Record(Serial, button, "30");
        clock.Now = T0.AddHours(3);
        var series = history.Record(Serial, button, "40")!;

        // Window is [T0+2h, T0+3h]: 10 and 20 are gone, 30 holds at the left edge until 40.
        Assert.Equal([new SparklinePoint(0, 30), new SparklinePoint(1, 40)], series.Points);
        Assert.Equal((30, 40), (series.Min, series.Max));
    }

    [Fact]
    public void Buffer_IsBounded_ForAChattyTopic()
    {
        // 10 points over 1 h → 6-minute buckets. One value per second for two hours.
        var (history, clock) = Create(maxPoints: 10);
        var button = Button(1);
        SparklineSeries? series = null;
        for (var s = 0; s < 7200; s++)
        {
            clock.Now = T0.AddSeconds(s);
            series = history.Record(Serial, button, s.ToString());
        }

        Assert.NotNull(series);
        Assert.InRange(series.Points.Count, 2, 12); // ≤ MaxPoints + the left-edge point + "now"
        Assert.Equal(7199, series.Points[^1].Value);
        Assert.True(series.Points.Zip(series.Points.Skip(1)).All(p => p.First.X <= p.Second.X));
    }

    [Fact]
    public void ValuesInTheSameBucket_ReplaceThePreviousOne()
    {
        var (history, clock) = Create(maxPoints: 10);  // 6-minute buckets for 1 h
        var button = Button(1);
        history.Record(Serial, button, "1");
        clock.Now = T0.AddMinutes(1);
        var series = history.Record(Serial, button, "2")!;

        Assert.All(series.Points, p => Assert.Equal(2, p.Value));
    }

    [Fact]
    public void Buffer_DropsTheOldestWhenFull()
    {
        var buffer = new SparklineBuffer(3);
        for (var i = 0; i < 5; i++) buffer.Add(T0.AddMinutes(i), i);

        Assert.Equal(3, buffer.Count);
        Assert.Equal([2.0, 3.0, 4.0], Enumerable.Range(0, buffer.Count).Select(i => buffer[i].Value));
    }

    [Fact]
    public void Buttons_HaveSeparateHistories()
    {
        var (history, _) = Create();
        history.Record(Serial, Button(6, "a"), "1");
        var b = history.Record(Serial, Button(6, "b"), "2")!;
        var other = history.Record("SN2", Button(6, "a"), "3")!;

        Assert.All(b.Points, p => Assert.Equal(2, p.Value));
        Assert.All(other.Points, p => Assert.Equal(3, p.Value));
    }
}
