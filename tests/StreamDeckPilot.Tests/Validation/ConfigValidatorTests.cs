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
            new DisplaySpec(null, null, null),
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
}
