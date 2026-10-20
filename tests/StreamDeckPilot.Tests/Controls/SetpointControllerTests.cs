using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Controls;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Tests.Validation;

namespace StreamDeckPilot.Tests.Controls;

/// <summary>#132: the setpoint's pending target, debounced commit, confirmation and timeout.</summary>
public class SetpointControllerTests
{
    private const string Serial = "SN1";
    private const int Columns = 5;
    // Vertical at anchor 0 on a 5-wide deck: + is key 0, value key 5, − key 10.
    private const int Plus = 0, Value = 5, Minus = 10;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly DesiredStateStore _desired = new();
    private readonly List<(string Topic, string Payload)> _published = [];
    private readonly List<(string PageId, int Key)> _changed = [];
    private BrokerIndicator? _broker;
    private SetpointController? _controller;
    private SetpointControl _control = SetpointValidationTests.Setpoint() with { Commit = new SetpointCommit("climate/room/setpoint/set", DelayMs: 1500), ConfirmTimeoutSeconds = 30 };

    private SetpointController Start(SetpointControl? control = null, bool publishSucceeds = true)
    {
        if (control is not null) _control = control;
        _controller = new SetpointController(_desired, Options.Create(new SetpointOptions()), _time, _broker);
        _controller.Publish = (topic, payload) =>
        {
            _published.Add((topic, payload));
            return Task.FromResult(publishSucceeds);
        };
        _controller.Changed += (_, pageId, key) => _changed.Add((pageId, key));
        _controller.Seed(Serial, new DeviceConfig(2, Serial, [new ButtonGridPage("main", [], [_control])]), Columns);
        return _controller;
    }

    private void Current(double v) => _controller!.ValueReceived(Serial, "main", _control, isTarget: false, v.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private void Target(double v) => _controller!.ValueReceived(Serial, "main", _control, isTarget: true, v.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private void Press(int key, string gesture = GestureNames.Tap) => _controller!.Press(Serial, "main", key, gesture);
    private void Advance(double ms) => _time.Advance(TimeSpan.FromMilliseconds(ms));

    private SetpointFace Face(int key) => _desired.Get(Serial, "main", key)!.Setpoint!;
    private ButtonRenderState ValueState => _desired.Get(Serial, "main", Value)!;

    // ── Seeding ─────────────────────────────────────────────────────────────

    [Fact]
    public void Seed_WritesTheThreeKeys_BeforeAnyValue()
    {
        Start();
        Assert.Equal(SetpointKeyRole.Plus, Face(Plus).Role);
        Assert.Equal(SetpointKeyRole.Value, Face(Value).Role);
        Assert.Equal(SetpointKeyRole.Minus, Face(Minus).Role);
        Assert.Null(Face(Value).Current);
        Assert.Equal("Office", Face(Value).Label);
        Assert.Equal("builtin:plus-thick", _desired.Get(Serial, "main", Plus)!.IconReference);
        Assert.True(ValueState.DependsOnBroker); // the offline look applies (#26)
    }

    [Fact]
    public void Seed_WithoutTheDeckWidth_WritesNothingYet()
    {
        _controller = new SetpointController(_desired, null, _time);
        _controller.Seed(Serial, new DeviceConfig(2, Serial, [new ButtonGridPage("main", [], [_control])]), columns: null);
        Assert.Empty(_desired.GetPage(Serial, "main"));
        Assert.False(_controller.TryFindKey(Serial, "main", Value, out _, out _));
    }

    [Fact]
    public void HorizontalKeys_AreMinusValuePlus()
    {
        Start(_control with { AnchorKey = 6, Orientation = SetpointOrientation.Horizontal });
        Assert.True(_controller!.TryFindKey(Serial, "main", 6, out _, out var left));
        Assert.True(_controller.TryFindKey(Serial, "main", 8, out _, out var right));
        Assert.Equal(SetpointKeyRole.Minus, left);
        Assert.Equal(SetpointKeyRole.Plus, right);
    }

    // ── Values and layout ────────────────────────────────────────────────────

    [Fact]
    public void Values_ShowCurrentLargeAndTargetSmall_WithThePrecisionOfTheStep()
    {
        Start();
        Current(21.37);
        Target(21);
        Assert.Equal("21.4°", Face(Value).Current);
        Assert.Equal("21.0°", Face(Value).Target);
        Assert.False(Face(Value).Pending);
    }

    [Fact]
    public void Rules_ColourTheValueKey_FromTheCurrentValue()
    {
        Start(_control with { Rules = [new ConditionalRule(">25", "#CC3300", null), new ConditionalRule("any", "#0066AA", null)] });
        Current(26);
        Target(20); // the target doesn't drive the colour
        Assert.Equal("#CC3300", ValueState.BackgroundColour);
        Current(22);
        Assert.Equal("#0066AA", ValueState.BackgroundColour);
    }

    [Fact]
    public void ArrowKeys_AreDisabled_AtTheEndsOfTheRange()
    {
        Start();
        Target(30);
        Assert.True(Face(Plus).Disabled);
        Assert.False(Face(Minus).Disabled);
        Target(5);
        Assert.False(Face(Plus).Disabled);
        Assert.True(Face(Minus).Disabled);
    }

    // ── Debounce ─────────────────────────────────────────────────────────────

    [Fact]
    public void SeveralPresses_InTheWindow_PublishOnce_WithTheFinalAbsoluteTarget()
    {
        Start();
        Target(21);
        Press(Plus);
        Advance(1000);
        Press(Plus);
        Advance(1000);
        Press(Plus);

        Assert.True(Face(Value).Pending); // shown at once
        Assert.Equal("22.5°", Face(Value).Target);
        Advance(1499);
        Assert.Empty(_published);         // the window restarts on every press

        Advance(1);
        Assert.Equal([("climate/room/setpoint/set", "22.5")], _published);
        Assert.True(Face(Value).Pending); // until the device reports it
    }

    [Fact]
    public void Press_ClampsToTheRange_AndSnapsToTheStep()
    {
        Start();
        Target(29.8);           // off the grid
        Press(Plus);
        Assert.Equal("30.0°", Face(Value).Target);
        Press(Plus);            // already at max: nothing changes
        Assert.Equal("30.0°", Face(Value).Target);
        Assert.True(Face(Plus).Disabled);
    }

    [Fact]
    public void Press_AtTheEndOfTheRange_DoesNothing()
    {
        Start();
        Target(5);
        Press(Minus);
        Assert.False(Face(Value).Pending);
        Advance(5000);
        Assert.Empty(_published);
    }

    [Fact]
    public void Press_WithNoValueKnown_IsIgnored()
    {
        Start();
        Press(Plus);
        Advance(5000);
        Assert.Empty(_published);
        Assert.False(Face(Value).Pending);
    }

    [Fact]
    public void Press_WithOnlyACurrentValue_IsIgnored_UntilATargetIsKnown()
    {
        // current + step could be far from the real setpoint (current 21, setpoint 16).
        Start();
        Current(20);
        Press(Minus);
        Assert.False(Face(Value).Pending);
        Advance(5000);
        Assert.Empty(_published);

        Target(16);
        Press(Minus);
        Assert.Equal("15.5°", Face(Value).Target);
    }

    [Fact]
    public void LongPress_MovesByLongPressStep()
    {
        Start(_control with { LongPressStep = 2 });
        Target(20);
        Press(Plus, GestureNames.LongPress);
        Assert.Equal("22.0°", Face(Value).Target);
        Press(Minus);
        Assert.Equal("21.5°", Face(Value).Target);
    }

    [Fact]
    public void ValueKey_Press_DoesNothing()
    {
        Start();
        Target(21);
        Press(Value);
        Advance(5000);
        Assert.Empty(_published);
    }

    [Fact]
    public void PayloadTemplate_FillsInTheTarget()
    {
        Start(_control with { Commit = new SetpointCommit("climate/room/setpoint/set", "{\"temperature\": {target}}", 1500) });
        Target(21);
        Press(Plus);
        Advance(1500);
        Assert.Equal("{\"temperature\": 21.5}", Assert.Single(_published).Payload);
    }

    [Fact]
    public void BackToTheReportedTarget_PublishesNothing_AndClearsThePendingLook()
    {
        Start();
        Target(21);
        Press(Plus);
        Press(Minus);
        Advance(1500);
        Assert.Empty(_published);
        Assert.False(Face(Value).Pending);
    }

    // ── Confirmation ─────────────────────────────────────────────────────────

    [Fact]
    public void MatchingTarget_AfterTheCommit_ClearsThePendingLook()
    {
        Start();
        Target(21);
        Press(Plus);
        Advance(1500);
        Target(21.5);
        Assert.False(Face(Value).Pending);
        Assert.Equal("21.5°", Face(Value).Target);

        Advance(60_000); // no late failure
        Assert.Equal(PressFeedback.None, ValueState.Feedback);
    }

    [Fact]
    public void TargetWithinHalfAStep_Confirms()
    {
        Start();
        Target(21);
        Press(Plus);
        Advance(1500);
        Target(21.6); // the device rounds differently
        Assert.False(Face(Value).Pending);
    }

    [Fact]
    public void TargetArrivingBeforeTheCommit_IsNotAConfirmation()
    {
        Start();
        Target(21);
        Press(Plus);
        Press(Plus);  // pending 22
        Target(21.5); // an echo of something else, while the user is still adjusting
        Assert.True(Face(Value).Pending);
        Assert.Equal("22.0°", Face(Value).Target);

        Advance(1500);
        Assert.Equal("22", Assert.Single(_published).Payload);
        Assert.True(Face(Value).Pending);
    }

    [Fact]
    public void NonMatchingTarget_KeepsWaiting_ThenTimesOut_FlashesFailed_AndReverts()
    {
        Start();
        Target(21);
        Press(Plus);
        Advance(1500);
        Target(20); // not what was committed
        Assert.True(Face(Value).Pending);

        Advance(30_000);
        Assert.Equal(PressFeedback.Failed, ValueState.Feedback);
        Assert.False(Face(Value).Pending);
        Assert.Equal("20.0°", Face(Value).Target); // the reported target shows again

        Advance(600);
        Assert.Equal(PressFeedback.None, ValueState.Feedback);
    }

    [Fact]
    public void NoReportAfterTheCommit_TimesOut()
    {
        Start();
        Target(20);
        Press(Plus);
        Advance(1500);
        Advance(29_999);
        Assert.True(Face(Value).Pending);
        Advance(1);
        Assert.Equal(PressFeedback.Failed, ValueState.Feedback);
    }

    [Fact]
    public void PressAfterTheCommit_StartsANewCycle()
    {
        Start();
        Target(21);
        Press(Plus);
        Advance(1500);              // published 21.5, awaiting confirmation
        Press(Plus);                // continues from the committed 21.5
        Assert.Equal("22.0°", Face(Value).Target);
        Advance(1500);
        Assert.Equal(["21.5", "22"], _published.Select(p => p.Payload));

        Advance(29_000);            // the first cycle's timeout was replaced: no failure yet
        Assert.Equal(PressFeedback.None, ValueState.Feedback);
        Target(22);
        Assert.False(Face(Value).Pending);
    }

    [Fact]
    public void RevertWhileACommandIsInFlight_IsPublished_AndALateReportOfTheOldTargetDoesNotConfirm()
    {
        // The reviewer's sequence: a slow device hasn't reported 16.5 yet when the user goes back to 16.
        Start();
        Target(16);
        Press(Plus);
        Advance(1500);                       // 16.5 in flight
        Press(Minus);                        // back to 16.0 before the device reports
        Advance(1500);
        Assert.Equal(["16.5", "16"], _published.Select(p => p.Payload));
        Assert.True(Face(Value).Pending);
        Assert.Equal("16.0°", Face(Value).Target);

        Target(16.5);                        // the superseded command lands late
        Assert.True(Face(Value).Pending);    // not a confirmation of 16.0
        Assert.Equal("16.0°", Face(Value).Target);

        Target(16);                          // the second command lands
        Assert.False(Face(Value).Pending);
        Assert.Equal("16.0°", Face(Value).Target);
    }

    [Fact]
    public void NewCommit_RestartsTheConfirmTimeout()
    {
        Start();
        Target(16);
        Press(Plus);
        Advance(1500);                       // 16.5 in flight, times out at +30 s
        Advance(20_000);
        Press(Plus);
        Advance(1500);                       // 17 supersedes it, with a fresh 30 s
        Advance(20_000);                     // past the first command's deadline
        Assert.Equal(PressFeedback.None, ValueState.Feedback);
        Assert.True(Face(Value).Pending);
        Advance(10_000);
        Assert.Equal(PressFeedback.Failed, ValueState.Feedback);
    }

    [Fact]
    public void BackToTheCommandInFlight_IsNotResent_AndStillWaitsForIt()
    {
        Start();
        Target(16);
        Press(Plus);
        Advance(1500);                       // 16.5 in flight
        Press(Plus);
        Press(Minus);                        // back to 16.5
        Advance(1500);
        Assert.Equal(["16.5"], _published.Select(p => p.Payload));
        Assert.True(Face(Value).Pending);

        Target(16.5);
        Assert.False(Face(Value).Pending);
    }

    [Fact]
    public void InFlightTargetArriving_WhileAdjustingAgain_KeepsTheBand()
    {
        Start();
        Target(16);
        Press(Plus);
        Advance(1500);                       // 16.5 in flight
        Press(Plus);                         // adjusting to 17
        Target(16.5);                        // the first command lands mid-adjustment
        Assert.True(Face(Value).Pending);
        Assert.Equal("17.0°", Face(Value).Target);
        Advance(1500);
        Assert.Equal(["16.5", "17"], _published.Select(p => p.Payload));
    }

    [Fact]
    public void FailedPublish_DropsThePendingTarget_WithoutAFlash()
    {
        Start(publishSucceeds: false);
        Target(21);
        Press(Plus);
        Advance(1500);
        Assert.False(Face(Value).Pending);
        Advance(60_000);
        Assert.Equal(PressFeedback.None, ValueState.Feedback);
    }

    [Fact]
    public void Timeout_WhileTheBrokerIsOffline_DoesNotFlash()
    {
        _broker = new BrokerIndicator(TimeSpan.Zero, _time);
        Start();
        Target(21);
        Press(Plus);
        Advance(1500);
        _broker.ReportDisconnected();
        Advance(30_000);
        Assert.Equal(PressFeedback.None, ValueState.Feedback);
        Assert.False(Face(Value).Pending);
    }

    [Fact]
    public void PendingSurvivesAPageSwitch_TheCommitStillHappens()
    {
        // The controller doesn't know which page is on screen; it only asks for repaints.
        Start();
        Target(21);
        Press(Plus);
        _changed.Clear();
        Advance(1500);
        Assert.Single(_published);
    }

    // ── Staleness and config ─────────────────────────────────────────────────

    [Fact]
    public void StaleCurrent_DimsTheValueKey_UntilAValueArrives()
    {
        Start(_control with { Current = new SetpointBinding("climate/room/current_temperature", StalenessTimeout: TimeSpan.FromMinutes(5)) });
        Current(21);
        _time.Advance(TimeSpan.FromMinutes(6));
        _controller!.CheckStaleness();
        Assert.True(ValueState.IsDimmed);
        Current(21.5);
        Assert.False(ValueState.IsDimmed);
    }

    [Fact]
    public void Reseed_KeepsValues_AndDropsRemovedControls()
    {
        Start();
        Current(21);
        _desired.Clear(Serial);
        _controller!.Seed(Serial, new DeviceConfig(2, Serial, [new ButtonGridPage("main", [], [_control])]), Columns);
        Assert.Equal("21.0°", Face(Value).Current);

        _controller.Seed(Serial, new DeviceConfig(2, Serial, [new ButtonGridPage("main", [])]), Columns);
        Assert.False(_controller.TryFindKey(Serial, "main", Value, out _, out _));
    }

    [Fact]
    public void Changes_AskForARepaintOfTheThreeKeys()
    {
        Start();
        Target(21);
        Assert.Equal([("main", Minus), ("main", Value), ("main", Plus)], _changed);
    }

    [Fact]
    public void Options_AreRangeChecked()
    {
        Assert.Null(new SetpointOptions().Error());
        Assert.Contains("PendingColour", new SetpointOptions { PendingColour = "amber" }.Error());
        Assert.Contains("DefaultCommitDelayMs", new SetpointOptions { DefaultCommitDelayMs = -1 }.Error());
        Assert.Contains("DefaultConfirmTimeoutSeconds", new SetpointOptions { DefaultConfirmTimeoutSeconds = 0 }.Error());
    }

    [Fact]
    public void Options_PendingColour_IsUsedOnTheValueKey()
    {
        _controller = new SetpointController(_desired, Options.Create(new SetpointOptions { PendingColour = "#00AAFF" }), _time);
        _controller.Seed(Serial, new DeviceConfig(2, Serial, [new ButtonGridPage("main", [], [_control])]), Columns);
        Assert.Equal("#00AAFF", Face(Value).PendingColour);
    }

    [Theory]
    [InlineData(21.3, 5, 30, 0.5, 21.5)]
    [InlineData(4, 5, 30, 0.5, 5)]
    [InlineData(31, 5, 30, 0.5, 30)]
    [InlineData(31, 5, 30, 2, 30)]     // 29 + 2: the top of a range the step doesn't divide is reached by clamping
    [InlineData(28, 5, 30, 2, 29)]     // 30 − 2 lands back on the grid
    [InlineData(0.30000000000000004, 0, 1, 0.1, 0.3)]
    public void Snap_ClampsAndRoundsToTheStepGrid(double value, double min, double max, double step, double expected) =>
        Assert.Equal(expected, SetpointController.Snap(value, _control with { Min = min, Max = max, Step = step }));
}
