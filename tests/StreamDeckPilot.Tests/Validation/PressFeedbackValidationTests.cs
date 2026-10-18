using System.Text.Json;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Validation;

namespace StreamDeckPilot.Tests.Validation;

/// <summary>#22: the optional per-button <c>display.pressFeedback</c> and <c>display.pressFeedbackTimeoutSeconds</c>.</summary>
public class PressFeedbackValidationTests
{
    private static readonly DeviceEntry Mk2 =
        new("SN001", "Stream Deck MK.2", 3, 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static DeviceConfig With(DisplaySpec display) =>
        new(2, "SN001", [new ButtonGridPage("main", [
            new ButtonDefinition("b1", 0, "main", display, null, [],
                new Dictionary<string, IReadOnlyList<ButtonAction>> { ["Tap"] = [new PublishAction("a/b", "1")] }),
        ])]);

    [Fact]
    public void Unset_IsValid() =>
        Assert.True(ConfigValidator.ValidateConfig(With(new DisplaySpec()), Mk2).IsValid);

    [Theory]
    [InlineData("auto")]
    [InlineData("ring")]
    [InlineData("off")]
    public void SupportedModes_AreValid(string mode) =>
        Assert.True(ConfigValidator.ValidateConfig(With(new DisplaySpec(PressFeedback: mode)), Mk2).IsValid);

    [Theory]
    [InlineData("Ring", "must be written 'ring'")]
    [InlineData("OFF", "must be written 'off'")]
    [InlineData("flash", "not supported (supported: auto, ring, off)")]
    [InlineData("", "not supported (supported: auto, ring, off)")]
    public void UnknownMode_IsRejected_WithAHint(string mode, string expectedFragment)
    {
        var error = Assert.Single(ConfigValidator.ValidateConfig(With(new DisplaySpec(PressFeedback: mode)), Mk2).Errors);
        Assert.Contains("'b1'", error);
        Assert.Contains($"display.pressFeedback '{mode}'", error);
        Assert.Contains(expectedFragment, error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(120)]
    public void Timeout_InRange_IsValid(double seconds) =>
        Assert.True(ConfigValidator.ValidateConfig(With(new DisplaySpec(PressFeedbackTimeoutSeconds: seconds)), Mk2).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(-3)]
    [InlineData(120.5)]
    [InlineData(double.NaN)]
    public void Timeout_OutOfRange_IsRejected(double seconds)
    {
        var error = Assert.Single(ConfigValidator.ValidateConfig(With(new DisplaySpec(PressFeedbackTimeoutSeconds: seconds)), Mk2).Errors);
        Assert.Contains("display.pressFeedbackTimeoutSeconds must be between 1 and 120", error);
    }

    // ── Serialisation ────────────────────────────────────────────────────────

    [Fact]
    public void Unset_IsLeftOutOfTheJson_SoExistingConfigsRoundTripUnchanged()
    {
        var json = JsonSerializer.Serialize(With(new DisplaySpec("builtin:flash", Precision: 1)), JsonOptions.Default);
        Assert.DoesNotContain("pressFeedback", json);
        Assert.DoesNotContain("sparkline", json);   // #129: was written as "sparkline": null

        var again = JsonSerializer.Serialize(JsonSerializer.Deserialize<DeviceConfig>(json, JsonOptions.Default), JsonOptions.Default);
        Assert.Equal(json, again);
    }

    [Fact]
    public void Set_RoundTrips()
    {
        var display = JsonSerializer.Deserialize<DisplaySpec>(
            """{"pressFeedback":"ring","pressFeedbackTimeoutSeconds":15}""", JsonOptions.Default)!;
        Assert.Equal("ring", display.PressFeedback);
        Assert.Equal(15, display.PressFeedbackTimeoutSeconds);

        var json = JsonSerializer.Serialize(display, JsonOptions.Default);
        Assert.Equal(display, JsonSerializer.Deserialize<DisplaySpec>(json, JsonOptions.Default));
    }
}
