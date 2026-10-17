using StreamDeckPilot.Infrastructure.Gestures;
using StreamDeckPilot.Tests.Brightness;

namespace StreamDeckPilot.Tests.Gestures;

public sealed class GestureDetectorTests : IDisposable
{
    private static readonly string[] TapOnly = ["Tap"];
    private static readonly string[] TapAndLong = ["Tap", "LongPress"];
    private static readonly string[] TapAndDouble = ["Tap", "DoubleTap"];
    private static readonly string[] All = ["Tap", "LongPress", "DoubleTap"];

    private readonly ManualTimeProvider _time = new();
    private readonly List<(int Key, long Generation)> _ticks = [];
    private readonly GestureDetector _detector;

    public GestureDetectorTests() =>
        _detector = new GestureDetector(new GestureOptions { LongPressMs = 500, DoubleTapMs = 300 }, _time,
            (key, generation) => _ticks.Add((key, generation)));

    public void Dispose() => _detector.Dispose();

    // Fires the armed timers and feeds their ticks back, as the press queue does.
    private List<GestureEvent> Elapse()
    {
        _ticks.Clear();
        _time.FireAll();
        return [.. _ticks.SelectMany(t => _detector.TimerElapsed(t.Key, t.Generation))];
    }

    private static GestureEvent G(int key, string gesture, string page = "main") => new(key, page, gesture);

    [Fact]
    public void TapOnlyKey_FiresTapOnKeyDown_WithoutArmingATimer()
    {
        Assert.Equal([G(0, "Tap")], _detector.KeyDown(0, "main", TapOnly));
        Assert.Empty(_time.Timers);
        Assert.Empty(_detector.KeyUp(0));
    }

    [Fact]
    public void KeyWithNoButton_StillGivesTapOnKeyDown() =>
        Assert.Equal([G(4, "Tap")], _detector.KeyDown(4, "main", []));

    [Fact]
    public void LongPressKey_ShortHold_IsATapOnRelease()
    {
        Assert.Empty(_detector.KeyDown(0, "main", TapAndLong));
        Assert.Equal(TimeSpan.FromMilliseconds(500), Assert.Single(_time.Timers).DueTime);

        Assert.Equal([G(0, "Tap")], _detector.KeyUp(0));
        Assert.Empty(Elapse()); // the disarmed long-press timer gives nothing
    }

    [Fact]
    public void LongPressKey_HeldPastThreshold_FiresWhileHeld_AndTheReleaseIsSilent()
    {
        _detector.KeyDown(0, "main", TapAndLong);

        Assert.Equal([G(0, "LongPress")], Elapse());
        Assert.Empty(_detector.KeyUp(0));
    }

    [Fact]
    public void LongPress_FiresOnlyOnce_PerHold()
    {
        _detector.KeyDown(0, "main", TapAndLong);
        Assert.Single(Elapse());
        Assert.Empty(Elapse());
    }

    [Fact]
    public void DoubleTapKey_SingleTap_FiresTapAfterTheWindow()
    {
        Assert.Empty(_detector.KeyDown(0, "main", TapAndDouble));
        Assert.Empty(_time.Timers); // no LongPress defined: nothing armed while held
        Assert.Empty(_detector.KeyUp(0));
        Assert.Equal(TimeSpan.FromMilliseconds(300), Assert.Single(_time.Timers).DueTime);

        Assert.Equal([G(0, "Tap")], Elapse());
    }

    [Fact]
    public void DoubleTapKey_SecondPressInsideTheWindow_FiresDoubleTapAtOnce_AndNoTap()
    {
        _detector.KeyDown(0, "main", TapAndDouble);
        _detector.KeyUp(0);

        Assert.Equal([G(0, "DoubleTap")], _detector.KeyDown(0, "main", TapAndDouble));
        Assert.Empty(_detector.KeyUp(0));
        Assert.Empty(Elapse());
    }

    [Fact]
    public void DoubleTapKey_SecondPressAfterTheWindow_IsTwoTaps()
    {
        _detector.KeyDown(0, "main", TapAndDouble);
        _detector.KeyUp(0);
        Assert.Equal([G(0, "Tap")], Elapse());

        _detector.KeyDown(0, "main", TapAndDouble);
        _detector.KeyUp(0);
        Assert.Equal([G(0, "Tap")], Elapse());
    }

    [Fact]
    public void PressOnAnotherKey_EndsThePendingDoubleTapWindow_SoTapsKeepPressOrder()
    {
        _detector.KeyDown(0, "main", TapAndDouble);
        _detector.KeyUp(0);

        Assert.Equal([G(0, "Tap"), G(1, "Tap")], _detector.KeyDown(1, "main", TapOnly));
        Assert.Empty(Elapse());
    }

    [Fact]
    public void SameKeyOnAnotherPage_ResolvesThePendingTapForItsOwnPage()
    {
        _detector.KeyDown(0, "main", TapAndDouble);
        _detector.KeyUp(0);

        Assert.Equal([G(0, "Tap"), G(0, "Tap", "two")], _detector.KeyDown(0, "two", TapOnly));
    }

    [Fact]
    public void AllGestures_ShortTap_WaitsForTheDoubleTapWindow()
    {
        _detector.KeyDown(0, "main", All);
        Assert.Empty(_detector.KeyUp(0));
        Assert.Equal([G(0, "Tap")], Elapse());
    }

    [Fact]
    public void AllGestures_Hold_IsALongPress_NotATap()
    {
        _detector.KeyDown(0, "main", All);
        Assert.Equal([G(0, "LongPress")], Elapse());
        Assert.Empty(_detector.KeyUp(0));
        Assert.Empty(Elapse()); // no double-tap window opened by that release
    }

    [Fact]
    public void AllGestures_DoubleTap_DoesNotAlsoLongPress()
    {
        _detector.KeyDown(0, "main", All);
        _detector.KeyUp(0);
        Assert.Equal([G(0, "DoubleTap")], _detector.KeyDown(0, "main", All));
        Assert.Empty(Elapse()); // second press held: no long press
        Assert.Empty(_detector.KeyUp(0));
    }

    [Fact]
    public void StaleTimerTick_IsIgnored()
    {
        _detector.KeyDown(0, "main", TapAndLong);
        var timer = Assert.Single(_time.Timers);
        _detector.KeyUp(0); // disarms; a tick already dispatched may still arrive

        _ticks.Clear();
        timer.FireStale();
        Assert.Empty(_ticks.SelectMany(t => _detector.TimerElapsed(t.Key, t.Generation)));
    }

    [Fact]
    public void ReleaseWithoutAPress_IsIgnored() =>
        Assert.Empty(_detector.KeyUp(3)); // e.g. the press only woke a dark deck

    [Fact]
    public void LostRelease_TheNextPressStartsAFreshGesture()
    {
        _detector.KeyDown(0, "main", TapAndLong); // release never arrives (device unplugged)
        Assert.Empty(_detector.KeyDown(0, "main", TapAndLong));
        Assert.Equal([G(0, "Tap")], _detector.KeyUp(0));
    }

    [Fact]
    public void ConfiguredTimings_AreUsed()
    {
        using var detector = new GestureDetector(new GestureOptions { LongPressMs = 1200, DoubleTapMs = 450 }, _time, (_, _) => { });
        detector.KeyDown(0, "main", All);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), _time.Timers[^1].DueTime);
        detector.KeyUp(0);
        Assert.Equal(TimeSpan.FromMilliseconds(450), _time.Timers[^1].DueTime);
    }
}
