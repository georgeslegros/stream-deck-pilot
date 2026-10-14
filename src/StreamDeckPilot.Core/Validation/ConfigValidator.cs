using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rules;

namespace StreamDeckPilot.Core.Validation;

public static class ConfigValidator
{
    public static ValidationResult ValidateConfig(DeviceConfig config, DeviceEntry device)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var pageIds = config.Pages.Select(p => p.PageId).ToHashSet();
        var seenPositions = new HashSet<(string pageId, int keyIndex)>();

        foreach (var page in config.Pages)
        {
            if (page is not ButtonGridPage grid)
                continue;

            foreach (var button in grid.Buttons)
            {
                if (button.KeyIndex < 0 || button.KeyIndex >= device.KeyCount)
                    errors.Add($"Button '{button.ButtonId}': key index {button.KeyIndex} is out of range for device with {device.KeyCount} keys.");

                var pos = (button.PageId, button.KeyIndex);
                if (!seenPositions.Add(pos))
                    errors.Add($"Button '{button.ButtonId}': duplicate position (page '{button.PageId}', key {button.KeyIndex}).");

                if (button.Display.Precision is < 0 or > DisplaySpec.MaxPrecision)
                    errors.Add($"Button '{button.ButtonId}': display.precision must be between 0 and {DisplaySpec.MaxPrecision} (got {button.Display.Precision}).");

                for (var i = 0; i < button.Rules.Count; i++)
                {
                    if (!RuleCondition.TryParse(button.Rules[i].Condition, out _, out var conditionError))
                        errors.Add($"Button '{button.ButtonId}' rule {i + 1}: {conditionError}.");
                    if (button.Rules[i].BackgroundColour is { } colour && !IsHexColour(colour))
                        errors.Add($"Button '{button.ButtonId}' rule {i + 1}: backgroundColour '{colour}' must be #RRGGBB (e.g. #CC3300).");
                }

                foreach (var (gesture, actions) in button.Gestures)
                    foreach (var action in actions)
                        if (action is NavigateAction nav && !pageIds.Contains(nav.TargetPageId))
                            errors.Add($"Button '{button.ButtonId}' gesture '{gesture}': navigate target '{nav.TargetPageId}' does not exist.");
            }
        }

        var reachablePages = new HashSet<string>();
        if (config.Pages.Count > 0)
        {
            reachablePages.Add(config.Pages[0].PageId);
            foreach (var page in config.Pages)
                if (page is ButtonGridPage grid)
                    foreach (var button in grid.Buttons)
                        foreach (var actions in button.Gestures.Values)
                            foreach (var action in actions)
                                if (action is NavigateAction nav)
                                    reachablePages.Add(nav.TargetPageId);
        }

        foreach (var pageId in pageIds.Except(reachablePages))
            warnings.Add($"Page '{pageId}' is unreachable by any navigation action.");

        return new ValidationResult(errors, warnings);
    }

    // The renderer understands #RRGGBB only; anything else silently fell back to the default grey.
    private static bool IsHexColour(string value) =>
        value.Length == 7 && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}
