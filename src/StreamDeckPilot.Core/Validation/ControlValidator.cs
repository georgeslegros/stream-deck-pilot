using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rules;

namespace StreamDeckPilot.Core.Validation;

/// <summary>Validates the <c>controls</c> of every page (#132). Called by <see cref="ConfigValidator"/>.</summary>
internal static class ControlValidator
{
    public static void Validate(DeviceConfig config, DeviceEntry device, List<string> errors, List<string> warnings)
    {
        var controlIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var grid in config.Pages.OfType<ButtonGridPage>())
        {
            if (grid.Controls is null) continue;

            // Every key a button or an earlier control already holds on this page.
            var taken = new Dictionary<int, string>();
            foreach (var button in grid.Buttons)
                taken.TryAdd(button.KeyIndex, $"button '{button.ButtonId}'");

            foreach (var control in grid.Controls)
            {
                // Null entries are reported by ConfigValidator's structural pass.
                if (control is null) continue;
                var name = $"Control '{control.ControlId}'";

                if (string.IsNullOrWhiteSpace(control.ControlId))
                    errors.Add($"Page '{grid.PageId}': a control has no controlId.");
                else if (!controlIds.Add(control.ControlId))
                    errors.Add($"{name}: duplicate controlId (control ids are unique across the device's pages).");

                switch (control)
                {
                    case SetpointControl setpoint:
                        ValidateSetpoint(setpoint, name, grid, device, taken, errors, warnings);
                        break;
                }
            }
        }
    }

    private static void ValidateSetpoint(SetpointControl c, string name, ButtonGridPage grid, DeviceEntry device,
        Dictionary<int, string> taken, List<string> errors, List<string> warnings)
    {
        if (SetpointGeometry.Error(c, device) is { } geometry)
            errors.Add($"{name}: {geometry}.");
        else
        {
            var (minus, value, plus) = c.Keys(device.KeyColumns);
            foreach (var key in new[] { minus, value, plus })
            {
                if (taken.TryGetValue(key, out var owner))
                    errors.Add($"{name}: key {key} on page '{grid.PageId}' is already used by {owner}.");
                else
                    taken[key] = $"control '{c.ControlId}'";
            }
        }

        if (c.Current is null || string.IsNullOrWhiteSpace(c.Current.Topic))
            errors.Add($"{name}: current.topic is required.");
        if (c.Target is null || string.IsNullOrWhiteSpace(c.Target.Topic))
            errors.Add($"{name}: target.topic is required.");
        if (c.Commit is null)
            errors.Add($"{name}: commit is required (at least commit.topic).");
        else
        {
            if (ConfigValidator.PublishTopicError(c.Commit.Topic) is { } topicError)
                errors.Add($"{name}: commit.topic {topicError}.");
            if (c.Commit.DelayMs is < 0 or > SetpointCommit.MaxDelayMs)
                errors.Add($"{name}: commit.delayMs must be between 0 and {SetpointCommit.MaxDelayMs} (got {c.Commit.DelayMs}).");
        }

        var numbersOk = true;
        foreach (var (field, number) in new[] { ("step", c.Step), ("min", c.Min), ("max", c.Max) })
        {
            if (!double.IsFinite(number))
            {
                errors.Add($"{name}: {field} must be a finite number.");
                numbersOk = false;
            }
        }
        if (numbersOk)
        {
            if (c.Step <= 0)
                errors.Add($"{name}: step must be greater than 0 (got {c.Step}).");
            if (c.Min >= c.Max)
                errors.Add($"{name}: min must be less than max (got min {c.Min}, max {c.Max}).");
            else if (c.Step > 0 && !IsWhole((c.Max - c.Min) / c.Step))
                warnings.Add($"{name}: step {c.Step} does not divide max - min ({c.Max - c.Min}); the top of the range is reached by clamping.");
        }

        if (c.LongPressStep is { } longStep && !(double.IsFinite(longStep) && longStep > 0))
            errors.Add($"{name}: longPressStep must be greater than 0 (got {longStep}).");

        if (c.Precision is < 0 or > DisplaySpec.MaxPrecision)
            errors.Add($"{name}: precision must be between 0 and {DisplaySpec.MaxPrecision} (got {c.Precision}).");

        if (c.ConfirmTimeoutSeconds is { } timeout
            && !(timeout >= SetpointControl.MinConfirmTimeoutSeconds && timeout <= SetpointControl.MaxConfirmTimeoutSeconds))
            errors.Add($"{name}: confirmTimeoutSeconds must be between {SetpointControl.MinConfirmTimeoutSeconds} and {SetpointControl.MaxConfirmTimeoutSeconds} (got {timeout}).");

        // Same grammar as a button's rules, evaluated on the current value.
        if (c.Rules is { } rules)
        {
            for (var i = 0; i < rules.Count; i++)
            {
                if (rules[i] is not { } rule)
                {
                    errors.Add($"{name} rule {i + 1} is null.");
                    continue;
                }
                if (!RuleCondition.TryParse(rule.Condition, out _, out var conditionError))
                    errors.Add($"{name} rule {i + 1}: {conditionError}.");
                if (rule.BackgroundColour is { } colour && !ConfigValidator.IsHexColour(colour))
                    errors.Add($"{name} rule {i + 1}: backgroundColour '{colour}' must be #RRGGBB (e.g. #CC3300).");
                if (IconReference.Validate(rule.Icon) is { } iconError)
                    errors.Add($"{name} rule {i + 1}: icon '{rule.Icon}' {iconError}.");
                if (rule.Blink is { } blink)
                    foreach (var blinkError in ConfigValidator.BlinkErrors(blink))
                        errors.Add($"{name} rule {i + 1}: {blinkError}.");
            }
        }
    }

    // Tolerant of binary floating point: 25 / 0.5 is exactly 50, but 0.3 / 0.1 is 2.9999999999999996.
    private static bool IsWhole(double x) => Math.Abs(x - Math.Round(x)) < 1e-9;
}
