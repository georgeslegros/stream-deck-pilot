using System.Globalization;
using System.Text.Json;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rules;

namespace StreamDeckPilot.Infrastructure.Mqtt.Pipeline;

public static class InboundPipeline
{
    // Step 2 — extract value, unit and live label. The payload format is auto-detected:
    //   plain text  (on, heat, unavailable)  → the trimmed text
    //   JSON scalar (23.5, "heat", true)     → the number / unquoted string / "true"|"false"
    //   JSON object                          → valueField (dot path) if set, else the "value" key
    // unitField/labelField are only read from objects. Value is null when nothing usable is found
    // (empty payload, JSON null/array, object without the field): the caller skips the update.
    public static (string? Value, string? Unit, string? Label) Extract(
        string payload, string? valueField, string? unitField, string? labelField)
    {
        var text = payload.Trim();
        if (text.Length == 0) return (null, null, null);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return (text, null, null); } // plain text

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (Scalar(root), null, null);

            var value = WalkPath(root, string.IsNullOrWhiteSpace(valueField) ? "value" : valueField);
            var unit = unitField is not null ? WalkPath(root, unitField) : null;
            var label = labelField is not null ? WalkPath(root, labelField) : null;
            return (value, unit, label);
        }
    }

    // Step 3 — evaluate ordered conditional rules, first-match-wins. Numeric conditions only
    // match numeric values; text conditions (eq:/ne:/in:) and "any" match on the text. A rule
    // whose condition does not parse never matches (the validator rejects those on PUT).
    public static (string? Colour, string? Icon) EvaluateRules(string? valueStr, IReadOnlyList<ConditionalRule> rules) =>
        MatchRule(valueStr, rules) is { } rule ? (rule.BackgroundColour, rule.Icon) : (null, null);

    // The first rule that matches, or null. Callers that need more than colour and icon (blink) use this.
    public static ConditionalRule? MatchRule(string? valueStr, IReadOnlyList<ConditionalRule> rules)
    {
        if (valueStr is null) return null;

        foreach (var rule in rules)
            if (RuleCondition.TryParse(rule.Condition, out var condition, out _) && condition!.Matches(valueStr))
                return rule;

        return null;
    }

    // Step 4 — format numeric value to string with the button's precision
    public static string FormatValue(string? valueStr, int? precision = null)
    {
        if (valueStr is null) return string.Empty;
        if (!RuleCondition.TryParseNumber(valueStr, out var d))
            return valueStr;

        // An explicit precision always shows that many decimals ("1.20" kW stays the same width
        // as "1.23" kW). Without one, whole numbers render without a decimal (e.g. "612" ppm,
        // "45" %) and fractional values keep one (e.g. "21.4" °C).
        var decimals = precision ?? (d == Math.Truncate(d) ? 0 : 1);
        var rounded = Math.Round(d, decimals, MidpointRounding.AwayFromZero);
        // Avoid "-0" / "-0.0" when a small negative value rounds to zero.
        if (rounded == 0) rounded = 0;
        return rounded.ToString($"F{decimals}", CultureInfo.InvariantCulture);
    }

    // Step 5 — resolve a text zone to the string it should display.
    // A Template is filled from {value}/{unit}/{label} when live data exists; with no live
    // data (nothing to resolve against) the static Label is the fallback. A zone with only
    // a Label always shows it. {label} is the live MQTT label field, not the static name.
    public static string? ResolveZone(TextZone? zone, bool hasData, string? value, string? unit, string? mqttLabel)
    {
        if (zone is null) return null;
        if (zone.Template is not null && hasData)
            return zone.Template
                .Replace("{value}", value ?? string.Empty)
                .Replace("{unit}", unit ?? string.Empty)
                .Replace("{label}", mqttLabel ?? string.Empty);
        return zone.Label;
    }

    private static string? WalkPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(segment, out current)) return null;
        }
        return Scalar(current);
    }

    private static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()?.Trim(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null, // null, array, object: no single value to show
    };
}
