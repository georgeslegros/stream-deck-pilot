using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Validation;

namespace StreamDeckPilot.Tests.Validation;

public class ConfigValidatorTests
{
    private static readonly DeviceEntry Mk2 =
        new("SN001", "Stream Deck MK.2", 3, 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ButtonDefinition Btn(string id, int key, string pageId,
        IReadOnlyList<ButtonAction>? tapActions = null) =>
        new(id, key, pageId,
            new DisplaySpec(),
            null, [],
            tapActions is null
                ? new Dictionary<string, IReadOnlyList<ButtonAction>>()
                : new Dictionary<string, IReadOnlyList<ButtonAction>> { ["Tap"] = tapActions });

    [Fact]
    public void ValidConfig_ReturnsSuccess()
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", [Btn("b1", 0, "main"), Btn("b2", 1, "main")])
        ]);
        var result = ConfigValidator.ValidateConfig(config, Mk2);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(-1, false)]
    [InlineData(4, false)]
    public void DisplayPrecision_MustBeZeroToThree(int? precision, bool valid)
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", [Btn("b1", 0, "main") with { Display = new DisplaySpec(Precision: precision) }])
        ]);
        var result = ConfigValidator.ValidateConfig(config, Mk2);
        Assert.Equal(valid, result.IsValid);
        if (!valid)
            Assert.Contains(result.Errors, e => e.Contains("display.precision"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("#CC3300", true)]
    [InlineData("#cc3300", true)]
    [InlineData("red", false)]
    [InlineData("#F00", false)]
    [InlineData("#GG0000", false)]
    [InlineData("#CC3300FF", false)]
    [InlineData("CC3300", false)]
    public void RuleBackgroundColour_MustBeHexRgb(string? colour, bool valid)
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", [Btn("b1", 0, "main") with { Rules = [new ConditionalRule(">0", colour, null)] }])
        ]);
        var result = ConfigValidator.ValidateConfig(config, Mk2);
        Assert.Equal(valid, result.IsValid);
        if (!valid)
            Assert.Contains(result.Errors, e => e.Contains("rule 1") && e.Contains("#RRGGBB"));
    }

    [Fact]
    public void KeyIndexOutOfRange_ReturnsError()
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", [Btn("b1", 15, "main")])  // 15 = out of range for 5x3
        ]);
        var result = ConfigValidator.ValidateConfig(config, Mk2);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("out of range"));
    }

    [Fact]
    public void DuplicatePosition_ReturnsError()
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", [Btn("b1", 0, "main"), Btn("b2", 0, "main")])
        ]);
        var result = ConfigValidator.ValidateConfig(config, Mk2);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("duplicate position"));
    }

    [Fact]
    public void NavigateToMissingPage_ReturnsError()
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", [
                Btn("b1", 0, "main", [new NavigateAction("ghost-page")])
            ])
        ]);
        var result = ConfigValidator.ValidateConfig(config, Mk2);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ghost-page"));
    }

    [Fact]
    public void UnreachablePage_ReturnsWarning_NotError()
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", []),
            new ButtonGridPage("orphan", [])
        ]);
        var result = ConfigValidator.ValidateConfig(config, Mk2);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("orphan"));
    }

    [Fact]
    public void NavigateToExistingPage_IsValid()
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", [
                Btn("b1", 0, "main", [new NavigateAction("page2")])
            ]),
            new ButtonGridPage("page2", [])
        ]);
        var result = ConfigValidator.ValidateConfig(config, Mk2);
        Assert.True(result.IsValid);
    }

    private static DeviceConfig WithPublish(string? topic, string? payload = "1") =>
        new(1, "SN001", [
            new ButtonGridPage("main", [
                Btn("b1", 0, "main", [new NavigateAction("main"), new PublishAction(topic!, payload!)]),
            ])
        ]);

    [Theory]
    [InlineData("home/fan/set")]
    [InlineData("a")]
    [InlineData("/leading/slash")]
    [InlineData("with space/ok")]
    public void PublishTopic_Valid(string topic) =>
        Assert.True(ConfigValidator.ValidateConfig(WithPublish(topic), Mk2).IsValid);

    [Fact]
    public void PublishPayload_Missing_IsValid() =>   // published as an empty message
        Assert.True(ConfigValidator.ValidateConfig(WithPublish("a/b", payload: null), Mk2).IsValid);

    // These used to be accepted and only failed at press time, where the press silently did nothing.
    [Theory]
    [InlineData(null, "is required")]
    [InlineData("", "is required")]
    [InlineData("   ", "is required")]
    [InlineData("a/#", "wildcards")]
    [InlineData("a/+/b", "wildcards")]
    [InlineData("#", "wildcards")]
    [InlineData("a\0b", "NUL")]
    public void PublishTopic_Invalid_ReportsButtonGestureAndAction(string? topic, string expectedFragment)
    {
        var result = ConfigValidator.ValidateConfig(WithPublish(topic), Mk2);
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("Button 'b1' gesture 'Tap' action 2: publish topic", error);
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void PublishTopic_TooLong_IsRejected()
    {
        var result = ConfigValidator.ValidateConfig(WithPublish(new string('é', 32_768)), Mk2);  // 65 536 bytes
        Assert.Contains(result.Errors, e => e.Contains("longer than 65 535 bytes"));
    }

    private static DeviceConfig WithGesture(string gesture) =>
        new(1, "SN001", [
            new ButtonGridPage("main", [
                Btn("b1", 0, "main") with
                {
                    Gestures = new Dictionary<string, IReadOnlyList<ButtonAction>> { [gesture] = [new PublishAction("a/b", "1")] },
                },
            ])
        ]);

    [Theory]
    [InlineData("Tap")]
    [InlineData("LongPress")]
    [InlineData("DoubleTap")]
    public void Gesture_Supported_IsValid(string gesture) =>
        Assert.True(ConfigValidator.ValidateConfig(WithGesture(gesture), Mk2).IsValid);

    [Fact]
    public void Gesture_AllThreeOnOneButton_IsValid()
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", [
                Btn("b1", 0, "main") with
                {
                    Gestures = new Dictionary<string, IReadOnlyList<ButtonAction>>
                    {
                        ["Tap"] = [new PublishAction("a/tap", "1")],
                        ["LongPress"] = [new PublishAction("a/long", "1")],
                        ["DoubleTap"] = [new PublishAction("a/double", "1")],
                    },
                },
            ])
        ]);
        var result = ConfigValidator.ValidateConfig(config, Mk2);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // Gestures are looked up by exact name at press time: anything else would be stored and never fire.
    [Theory]
    [InlineData("tap", "must be written 'Tap'")]
    [InlineData("TAP", "must be written 'Tap'")]
    [InlineData("longpress", "must be written 'LongPress'")]
    [InlineData("doubleTap", "must be written 'DoubleTap'")]
    [InlineData("DoublePress", "not supported (supported: Tap, LongPress, DoubleTap)")]
    [InlineData("Hold", "not supported (supported: Tap, LongPress, DoubleTap)")]
    [InlineData("", "not supported (supported: Tap, LongPress, DoubleTap)")]
    public void Gesture_Unsupported_ReportsButtonAndName(string gesture, string expectedFragment)
    {
        var result = ConfigValidator.ValidateConfig(WithGesture(gesture), Mk2);
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'b1'", error);
        Assert.Contains($"gesture '{gesture}'", error);
        Assert.Contains(expectedFragment, error);
    }

    private static DeviceConfig WithRules(params string[] conditions) =>
        new(1, "SN001", [
            new ButtonGridPage("main", [
                Btn("b1", 0, "main") with { Rules = conditions.Select(c => new ConditionalRule(c, "#FF0000", null)).ToList() },
            ])
        ]);

    [Fact]
    public void Rules_NumericTextAndAny_AreValid()
    {
        var result = ConfigValidator.ValidateConfig(
            WithRules(">1000", "between:18:24", "==0", "eq:on", "ne:unavailable", "in:heat,cool", "any"), Mk2);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Rules_InvalidCondition_ReportsButtonAndRuleNumber()
    {
        var result = ConfigValidator.ValidateConfig(WithRules(">=0", "==on"), Mk2);
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'b1' rule 2", error);
        Assert.Contains("eq:on", error);
    }
}
