using Microsoft.Extensions.Options;
using StreamDeckPilot.Infrastructure.Brightness;

namespace StreamDeckPilot.Tests.Brightness;

public class BrightnessControllerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly List<int> _changes = [];

    private BrightnessController Build(int wakeTimeoutSeconds = 60)
    {
        var controller = new BrightnessController(
            Options.Create(new BrightnessOptions { WakeTimeoutSeconds = wakeTimeoutSeconds }),
            timeProvider: _time);
        controller.EffectiveChanged += _changes.Add;
        return controller;
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData(" 7 \n", 7)]
    [InlineData("42.6", 43)]
    [InlineData("150", 150)]          // clamping is the caller's job
    [InlineData("-3", -3)]
    [InlineData("{\"value\": 10}", 10)]
    [InlineData("{\"value\": \"55\"}", 55)]
    public void TryParse_AcceptsIntegerOrValueObject(string payload, int expected)
    {
        Assert.True(BrightnessController.TryParse(payload, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bright")]
    [InlineData("NaN")]
    [InlineData("[50]")]
    [InlineData("{\"level\": 50}")]
    [InlineData("{\"value\": true}")]
    [InlineData("{\"value\": \"high\"}")]
    [InlineData("{not json")]
    public void TryParse_RejectsGarbage(string payload) =>
        Assert.False(BrightnessController.TryParse(payload, out _));

    [Fact]
    public void Default_Is80()
    {
        using var c = Build();
        Assert.Equal(80, c.Effective);
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    [InlineData(35, 35)]
    public void SetDesired_ClampsAndRaisesChange(int requested, int applied)
    {
        using var c = Build();
        c.SetDesired(requested);
        Assert.Equal(applied, c.Effective);
        Assert.Equal([applied], _changes);
    }

    [Fact]
    public void SetDesired_SameValue_RaisesNothing()
    {
        using var c = Build();
        c.SetDesired(80);
        Assert.Empty(_changes);
    }

    [Fact]
    public void KeyPress_WhileLit_IsNotSwallowed()
    {
        using var c = Build();
        Assert.False(c.OnKeyPressed());
        Assert.Empty(_changes);
    }

    [Fact]
    public void KeyPress_WhileDark_WakesToLastNonZero_ThenTimesOut()
    {
        using var c = Build(wakeTimeoutSeconds: 45);
        c.SetDesired(30);
        c.SetDesired(0);
        _changes.Clear();

        Assert.True(c.OnKeyPressed());
        Assert.Equal(30, c.Effective);
        Assert.Equal([30], _changes);

        // Presses while awake run normally.
        Assert.False(c.OnKeyPressed());

        _time.FireAll();
        Assert.Equal(0, c.Effective);
        Assert.Equal([30, 0], _changes);
    }

    [Fact]
    public void KeyPress_WhileDark_WithNoEarlierLevel_WakesTo80()
    {
        using var c = Build();
        c.SetDesired(0);
        Assert.True(c.OnKeyPressed());
        Assert.Equal(80, c.Effective);
    }

    [Fact]
    public void KeyPress_WhileAwake_RestartsTimeout()
    {
        using var c = Build(wakeTimeoutSeconds: 45);
        c.SetDesired(0);
        c.OnKeyPressed();
        var first = Assert.Single(_time.Timers);
        first.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan); // simulate time passing

        c.OnKeyPressed();
        // Each arm gets a fresh timer (tagged with its generation, see #70); the old one is cancelled.
        Assert.Null(first.DueTime);
        Assert.Equal(TimeSpan.FromSeconds(45), _time.Timers[^1].DueTime);
    }

    [Fact]
    public void RedeliveredZero_DuringWake_DoesNotCutItShort()
    {
        // The retained set topic is redelivered on every broker reconnect.
        using var c = Build();
        c.SetDesired(0);
        c.OnKeyPressed();

        c.SetDesired(0);
        Assert.Equal(80, c.Effective);
    }

    [Fact]
    public void NewLevel_DuringWake_WinsAndTimeoutDoesNothing()
    {
        using var c = Build();
        c.SetDesired(0);
        c.OnKeyPressed();

        c.SetDesired(50);
        Assert.Equal(50, c.Effective);

        _changes.Clear();
        _time.FireAll();
        Assert.Equal(50, c.Effective);
        Assert.Empty(_changes);
    }

    [Fact]
    public void StaleWakeCallback_AfterAPressReArmed_DoesNotEndTheWake()
    {
        // #70: the timeout fires, its callback is dispatched, and a press re-arms the timer before
        // the callback takes the lock. The press was accepted, so the deck must stay awake.
        using var c = Build(wakeTimeoutSeconds: 45);
        c.SetDesired(30);
        c.SetDesired(0);
        Assert.True(c.OnKeyPressed());
        var firstTimer = _time.Timers[^1];

        Assert.False(c.OnKeyPressed()); // accepted press re-arms the timeout
        firstTimer.FireStale();         // the earlier, already-dispatched callback runs late

        Assert.Equal(30, c.Effective);

        _time.FireAll();                // the re-armed timeout still ends the wake
        Assert.Equal(0, c.Effective);
    }
}
