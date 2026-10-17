using OpenMacroBoard.SDK;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Tests.Brightness;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Rendering;

/// <summary>#22: pending look on press, cleared by the key's next value, red flash on timeout.</summary>
public sealed class PressFeedbackTrackerTests
{
    private const string Serial = "SN1";
    private readonly ManualTimeProvider _time = new();
    private readonly List<(string Serial, string PageId, int KeyIndex)> _changes = [];

    private PressFeedbackTracker Tracker(PressFeedbackOptions? options = null, BrokerIndicator? broker = null)
    {
        var tracker = new PressFeedbackTracker(options ?? new PressFeedbackOptions(), _time, broker);
        tracker.Changed += (s, p, k) => _changes.Add((s, p, k));
        return tracker;
    }

    private PressFeedback Feedback(PressFeedbackTracker tracker) => tracker.Get(Serial, "b3");

    [Fact]
    public void Press_IsPending_ThenFailsAtTheTimeout_ThenClearsAfterTheFlash()
    {
        var tracker = Tracker();

        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);
        Assert.Equal(PressFeedback.Pending, Feedback(tracker));
        Assert.Equal(TimeSpan.FromSeconds(3), _time.Timers[^1].DueTime);

        _time.FireAll();
        Assert.Equal(PressFeedback.Failed, Feedback(tracker));
        Assert.Equal(TimeSpan.FromMilliseconds(600), _time.Timers[^1].DueTime);

        _time.FireAll();
        Assert.Equal(PressFeedback.None, Feedback(tracker));
        Assert.Equal([(Serial, "main", 3), (Serial, "main", 3), (Serial, "main", 3)], _changes);
    }

    [Fact]
    public void StateArriving_ClearsThePendingLook_AndTheTimeoutNoLongerFlashes()
    {
        var tracker = Tracker();
        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);

        tracker.StateReceived(Serial, "b3");
        Assert.Equal(PressFeedback.None, Feedback(tracker));

        // Even a timeout callback already on its way is ignored.
        foreach (var t in _time.Timers) t.FireStale();
        Assert.Equal(PressFeedback.None, Feedback(tracker));
        Assert.Single(_changes); // only the press: the caller paints the new value itself
    }

    [Fact]
    public void KeyWithoutInboundTopic_ShowsPendingBriefly_AndNeverFlashesRed()
    {
        var tracker = Tracker();
        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: false);
        Assert.Equal(PressFeedback.Pending, Feedback(tracker));
        Assert.Equal(TimeSpan.FromMilliseconds(600), _time.Timers[^1].DueTime);

        _time.FireAll();
        Assert.Equal(PressFeedback.None, Feedback(tracker));
        _time.FireAll();
        Assert.Equal(PressFeedback.None, Feedback(tracker));
    }

    [Fact]
    public void PressAgain_RestartsTheWait()
    {
        var tracker = Tracker();
        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);
        var first = _time.Timers[^1];
        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);

        first.FireStale(); // the first press's timeout must not fail the second press
        Assert.Equal(PressFeedback.Pending, Feedback(tracker));
    }

    [Fact]
    public void BrokerOffline_NoFeedback()
    {
        var broker = new BrokerIndicator(TimeSpan.Zero);
        broker.ReportDisconnected();
        var tracker = Tracker(broker: broker);

        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);

        Assert.Equal(PressFeedback.None, Feedback(tracker));
        Assert.Empty(_time.Timers);
        Assert.Empty(_changes);
    }

    [Fact]
    public void BrokerGoingOfflineWhilePending_ClearsWithoutAFlash()
    {
        var broker = new BrokerIndicator(TimeSpan.Zero);
        var tracker = Tracker(broker: broker);
        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);

        broker.ReportDisconnected();
        Assert.Equal(PressFeedback.None, Feedback(tracker));
        Assert.Equal(2, _changes.Count); // pressed, then cleared

        foreach (var t in _time.Timers) t.FireStale();
        Assert.Equal(PressFeedback.None, Feedback(tracker));
    }

    [Fact]
    public void TimeoutWhileTheBrokerIsOffline_DoesNotFlash()
    {
        // The overlay's own delay hasn't elapsed when the press goes out, but has by the timeout.
        var broker = new BrokerIndicator(TimeSpan.FromSeconds(3), _time);
        var tracker = Tracker(broker: broker);
        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);
        var timeout = _time.Timers[^1];
        broker.ReportDisconnected();
        _time.Timers[^1].FireStale(); // the indicator turns on (and clears all feedback)

        timeout.FireStale();
        Assert.Equal(PressFeedback.None, Feedback(tracker));
    }

    [Fact]
    public void Disabled_NoFeedback()
    {
        var tracker = Tracker(new PressFeedbackOptions { Enabled = false });
        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);
        Assert.Equal(PressFeedback.None, Feedback(tracker));
    }

    // ── Renderer overlay ─────────────────────────────────────────────────────

    private static ButtonRenderState State(bool dependsOnBroker = true) =>
        new("b3", "#00AA00", null, IconPlacement.Corner, "42", null, DependsOnBroker: dependsOnBroker);

    [Fact]
    public void Renderer_DrawsTheFeedback_WithoutChangingDesiredState()
    {
        var tracker = Tracker();
        var renderer = new DeviceRenderer(feedback: tracker);
        var board = new FakeMacroBoard();
        var store = new DesiredStateStore();
        store.Set(Serial, "main", 3, State());

        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);
        renderer.RenderLatest(board, Serial, "main", 3, store);
        _time.FireAll();
        renderer.RenderLatest(board, Serial, "main", 3, store);
        _time.FireAll();
        renderer.RenderLatest(board, Serial, "main", 3, store);

        Assert.Equal(
            [KeyBitmap.Create.FromRgb(255, 255, 255), KeyBitmap.Create.FromRgb(200, 0, 0), KeyBitmap.Create.FromRgb(0, 0xAA, 0)],
            board.RenderCalls.Select(c => c.Bitmap));
        Assert.Equal(PressFeedback.None, store.Get(Serial, "main", 3)!.Feedback);
    }

    [Fact]
    public void Renderer_OfflineLookWinsOverFeedback()
    {
        var tracker = Tracker(); // pending, while the renderer's indicator is on
        tracker.Pressed(Serial, "main", 3, "b3", awaitsState: true);
        var offline = new BrokerIndicator(TimeSpan.Zero);
        offline.ReportDisconnected();
        var renderer = new DeviceRenderer(offline, tracker);
        var board = new FakeMacroBoard();

        renderer.RenderButton(board, Serial, 3, State());

        Assert.Equal(KeyBitmap.Create.FromRgb(30, 30, 30), Assert.Single(board.RenderCalls).Bitmap);
    }
}
