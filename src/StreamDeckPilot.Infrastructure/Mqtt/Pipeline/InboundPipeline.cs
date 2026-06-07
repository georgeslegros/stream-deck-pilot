using System.Globalization;
using System.Text.Json;
using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Infrastructure.Mqtt.Pipeline;

public static class InboundPipeline
{
    // Step 2 — extract value, unit and live label from JSON payload (path-lite) or bare string
    public static (string? Value, string? Unit, string? Label) Extract(
        string payload, string? valueField, string? unitField, string? labelField)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var value = valueField is not null ? WalkPath(doc.RootElement, valueField) : null;
            var unit = unitField is not null ? WalkPath(doc.RootElement, unitField) : null;
            var label = labelField is not null ? WalkPath(doc.RootElement, labelField) : null;
            return (value ?? payload.Trim(), unit, label);
        }
        catch (JsonException)
        {
            return (payload.Trim(), null, null);
        }
    }

    // Step 3 — evaluate ordered conditional rules, first-match-wins
    public static (string? Colour, string? Icon) EvaluateRules(string? valueStr, IReadOnlyList<ConditionalRule> rules)
    {
        if (valueStr is null
            || !double.TryParse(valueStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var numeric))
            return (null, null);

        foreach (var rule in rules)
            if (MatchesCondition(rule.Condition, numeric))
                return (rule.BackgroundColour, rule.Icon);

        return (null, null);
    }

    // Step 4 — format numeric value to string with given precision
    public static string FormatValue(string? valueStr, int precision = 1)
    {
        if (valueStr is null) return string.Empty;
        if (!double.TryParse(valueStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return valueStr;
        // Whole numbers render without a decimal (e.g. "612" ppm, "45" %); fractional
        // values keep `precision` decimals (e.g. "21.4" °C).
        return d == Math.Truncate(d)
            ? d.ToString("F0", CultureInfo.InvariantCulture)
            : d.ToString($"F{precision}", CultureInfo.InvariantCulture);
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

    private static bool MatchesCondition(string condition, double value)
    {
        if (condition.StartsWith(">=", StringComparison.Ordinal)) return value >= ParseNum(condition[2..]);
        if (condition.StartsWith(">", StringComparison.Ordinal)) return value > ParseNum(condition[1..]);
        if (condition.StartsWith("<=", StringComparison.Ordinal)) return value <= ParseNum(condition[2..]);
        if (condition.StartsWith("<", StringComparison.Ordinal)) return value < ParseNum(condition[1..]);
        if (condition.StartsWith("==", StringComparison.Ordinal)) return value == ParseNum(condition[2..]);
        if (condition.StartsWith("between:", StringComparison.Ordinal))
        {
            var parts = condition.Split(':');
            return parts.Length == 3
                   && value >= ParseNum(parts[1])
                   && value <= ParseNum(parts[2]);
        }
        return false;
    }

    private static double ParseNum(string s) =>
        double.Parse(s.Trim(), CultureInfo.InvariantCulture);

    private static string? WalkPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(segment, out current)) return null;
        }
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}
