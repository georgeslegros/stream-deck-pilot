using System.Text.Json;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Validation;

namespace StreamDeckPilot.Tests.Validation;

/// <summary>#132: the optional per-page <c>controls</c>, and the Setpoint control's validation.</summary>
public class SetpointValidationTests
{
    // 5 columns × 3 rows, like a Stream Deck MK.2.
    private static readonly DeviceEntry Mk2 =
        new("SN001", "Stream Deck MK.2", 3, 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    internal static SetpointControl Setpoint(
        string id = "room-1", int anchor = 0, SetpointOrientation orientation = SetpointOrientation.Vertical) =>
        new(id, anchor, orientation,
            new SetpointBinding("climate/room/current_temperature"),
            new SetpointBinding("climate/room/temperature"),
            new SetpointCommit("climate/room/setpoint/set"),
            Step: 0.5, Min: 5, Max: 30, Label: "Office", Unit: "°");

    private static ButtonDefinition Button(int keyIndex) =>
        new($"b{keyIndex}", keyIndex, "main", new DisplaySpec(), null, [],
            new Dictionary<string, IReadOnlyList<ButtonAction>> { ["Tap"] = [new PublishAction("a/b", "1")] });

    private static DeviceConfig Config(IReadOnlyList<ButtonDefinition> buttons, params Control[] controls) =>
        new(2, "SN001", [new ButtonGridPage("main", buttons, controls)]);

    private static ValidationResult Validate(params Control[] controls) =>
        ConfigValidator.ValidateConfig(Config([], controls), Mk2);

    private static void AssertSingleError(ValidationResult result, string fragment)
    {
        var error = Assert.Single(result.Errors);
        Assert.Contains(fragment, error);
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, SetpointOrientation.Vertical)]     // keys 0, 5, 10: one column, top to bottom
    [InlineData(4, SetpointOrientation.Vertical)]     // last column
    [InlineData(0, SetpointOrientation.Horizontal)]   // keys 0, 1, 2
    [InlineData(12, SetpointOrientation.Horizontal)]  // keys 12, 13, 14: the end of the last row
    public void KeysThatFit_AreValid(int anchor, SetpointOrientation orientation)
    {
        var result = Validate(Setpoint(anchor: anchor, orientation: orientation));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData(3, "in column 4 of 5")]   // 3, 4 would wrap to 5 on the next row
    [InlineData(4, "in column 5 of 5")]
    [InlineData(13, "in column 4 of 5")]
    public void Horizontal_ThatWouldWrapToTheNextRow_IsRejected(int anchor, string fragment) =>
        AssertSingleError(Validate(Setpoint(anchor: anchor, orientation: SetpointOrientation.Horizontal)), fragment);

    [Theory]
    [InlineData(5, "in row 2 of 3")]      // 5, 10, 15: 15 is past the grid
    [InlineData(10, "in row 3 of 3")]
    public void Vertical_ThatRunsOffTheGrid_IsRejected(int anchor, string fragment) =>
        AssertSingleError(Validate(Setpoint(anchor: anchor)), fragment);

    [Theory]
    [InlineData(-1)]
    [InlineData(15)]
    public void AnchorOutsideTheDevice_IsRejected(int anchor) =>
        AssertSingleError(Validate(Setpoint(anchor: anchor, orientation: SetpointOrientation.Horizontal)), "out of range");

    [Fact]
    public void Keys_FollowTheOrientation()
    {
        Assert.Equal((10, 5, 0), Setpoint(anchor: 0).Keys(5));   // vertical: + on top, − at the bottom
        Assert.Equal((6, 7, 8), Setpoint(anchor: 6, orientation: SetpointOrientation.Horizontal).Keys(5)); // − value +
    }

    // ── Overlap ──────────────────────────────────────────────────────────────

    [Fact]
    public void Overlap_WithAButton_IsRejected()
    {
        var result = ConfigValidator.ValidateConfig(Config([Button(5)], Setpoint(anchor: 0)), Mk2);
        AssertSingleError(result, "key 5 on page 'main' is already used by button 'b5'");
    }

    [Fact]
    public void Overlap_WithAnotherControl_IsRejected()
    {
        var result = Validate(Setpoint("a", 0, SetpointOrientation.Horizontal), Setpoint("b", 2, SetpointOrientation.Horizontal));
        AssertSingleError(result, "key 2 on page 'main' is already used by control 'a'");
    }

    [Fact]
    public void ButtonsOnOtherKeys_AreFine()
    {
        var result = ConfigValidator.ValidateConfig(Config([Button(1), Button(14)], Setpoint(anchor: 0)), Mk2);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void DuplicateControlId_IsRejected()
    {
        var config = new DeviceConfig(2, "SN001", [
            new ButtonGridPage("main", [], [Setpoint("room", 0)]),
            new ButtonGridPage("two", [], [Setpoint("room", 0)]),
        ]);
        Assert.Contains(ConfigValidator.ValidateConfig(config, Mk2).Errors, e => e.Contains("duplicate controlId"));
    }

    [Fact]
    public void NullControl_IsRejected() =>
        AssertSingleError(ConfigValidator.ValidateConfig(Config([], [null!]), Mk2), "control 1 is null");

    // ── Fields ───────────────────────────────────────────────────────────────

    [Fact]
    public void MinNotBelowMax_IsRejected() =>
        AssertSingleError(Validate(Setpoint() with { Min = 30, Max = 30 }), "min must be less than max");

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    public void NonPositiveStep_IsRejected(double step) =>
        AssertSingleError(Validate(Setpoint() with { Step = step }), "step must be greater than 0");

    [Fact]
    public void NonFiniteNumber_IsRejected() =>
        AssertSingleError(Validate(Setpoint() with { Max = double.PositiveInfinity }), "max must be a finite number");

    [Fact]
    public void StepThatDoesNotDivideTheRange_IsOnlyAWarning()
    {
        var result = Validate(Setpoint() with { Step = 2 }); // 30 - 5 = 25
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("does not divide"));
    }

    [Fact]
    public void FractionalStepThatDividesTheRange_HasNoWarning() =>
        Assert.Empty(Validate(Setpoint() with { Min = 0.3, Max = 0.9, Step = 0.1 }).Warnings);

    [Fact]
    public void MissingTopics_AreRejected()
    {
        var result = Validate(Setpoint() with
        {
            Current = null!,
            Target = new SetpointBinding(" "),
            Commit = new SetpointCommit(""),
        });
        Assert.Contains(result.Errors, e => e.Contains("current.topic is required"));
        Assert.Contains(result.Errors, e => e.Contains("target.topic is required"));
        Assert.Contains(result.Errors, e => e.Contains("commit.topic is required"));
    }

    [Fact]
    public void MissingCommit_IsRejected() =>
        AssertSingleError(Validate(Setpoint() with { Commit = null! }), "commit is required");

    [Fact]
    public void WildcardCommitTopic_IsRejected() =>
        AssertSingleError(Validate(Setpoint() with { Commit = new SetpointCommit("climate/+/set") }), "must not contain the wildcards");

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void CommitDelayOutOfRange_IsRejected(int delay) =>
        AssertSingleError(Validate(Setpoint() with { Commit = new SetpointCommit("a/set", DelayMs: delay) }), "commit.delayMs must be between 0 and 10000");

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void ConfirmTimeoutOutOfRange_IsRejected(double seconds) =>
        AssertSingleError(Validate(Setpoint() with { ConfirmTimeoutSeconds = seconds }), "confirmTimeoutSeconds must be between 1 and 600");

    [Fact]
    public void NonPositiveLongPressStep_IsRejected() =>
        AssertSingleError(Validate(Setpoint() with { LongPressStep = 0 }), "longPressStep must be greater than 0");

    [Fact]
    public void PrecisionOutOfRange_IsRejected() =>
        AssertSingleError(Validate(Setpoint() with { Precision = 4 }), "precision must be between 0 and 3");

    [Fact]
    public void Rules_UseTheButtonRuleGrammar()
    {
        var result = Validate(Setpoint() with
        {
            Rules = [new ConditionalRule(">=nope", null, null), new ConditionalRule(">25", "red", null)],
        });
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("rule 1"));
        Assert.Contains(result.Errors, e => e.Contains("rule 2: backgroundColour 'red' must be #RRGGBB"));
    }

    [Fact]
    public void RuleBlink_IsValidatedLikeAButtonRule() =>
        AssertSingleError(Validate(Setpoint() with
        {
            Rules = [new ConditionalRule(">25", "#CC3300", null, new BlinkSpec(Style: "Pulse"))],
        }), "rule 1: blink.style 'Pulse' must be written 'pulse'");

    // ── JSON ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PageWithoutControls_LeavesThemOutOfTheJson_SoExistingConfigsRoundTripUnchanged()
    {
        var config = Config([Button(0)]) with { Pages = [new ButtonGridPage("main", [Button(0)])] };
        var json = JsonSerializer.Serialize(config, JsonOptions.Default);
        Assert.DoesNotContain("controls", json);

        var again = JsonSerializer.Serialize(JsonSerializer.Deserialize<DeviceConfig>(json, JsonOptions.Default), JsonOptions.Default);
        Assert.Equal(json, again);
    }

    [Fact]
    public void Setpoint_RoundTrips_WithItsDiscriminatorAndLowerCaseOrientation()
    {
        var json = """
            {
              "schemaVersion": 2,
              "serial": "SN001",
              "pages": [
                {
                  "pageType": "ButtonGrid",
                  "pageId": "main",
                  "buttons": [],
                  "controls": [
                    {
                      "type": "Setpoint",
                      "controlId": "room-1",
                      "anchorKey": 0,
                      "orientation": "vertical",
                      "current": { "topic": "climate/room/current_temperature" },
                      "target": { "topic": "climate/room/temperature", "valueField": "temperature" },
                      "commit": { "topic": "climate/room/setpoint/set", "payload": "{\"temperature\": {target}}", "delayMs": 1500 },
                      "step": 0.5,
                      "min": 5,
                      "max": 30,
                      "unit": "°",
                      "longPressStep": 2
                    }
                  ]
                }
              ]
            }
            """;
        var config = JsonSerializer.Deserialize<DeviceConfig>(json, JsonOptions.Default)!;
        var setpoint = Assert.IsType<SetpointControl>(Assert.Single(((ButtonGridPage)config.Pages[0]).Controls!));
        Assert.Equal(SetpointOrientation.Vertical, setpoint.Orientation);
        Assert.Equal("temperature", setpoint.Target.ValueField);
        Assert.Equal(2, setpoint.LongPressStep);
        Assert.True(ConfigValidator.ValidateConfig(config, Mk2).IsValid);

        var written = JsonSerializer.Serialize(config, JsonOptions.Default);
        Assert.Contains("\"type\": \"Setpoint\"", written);
        Assert.Contains("\"orientation\": \"vertical\"", written);
        Assert.DoesNotContain("\"label\"", written); // unset optionals are left out
        Assert.Equal(written, JsonSerializer.Serialize(JsonSerializer.Deserialize<DeviceConfig>(written, JsonOptions.Default), JsonOptions.Default));
    }
}
