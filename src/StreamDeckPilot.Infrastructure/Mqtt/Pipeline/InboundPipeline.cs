using System.Globalization;
using System.Text.Json;
using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Infrastructure.Mqtt.Pipeline;

public static class InboundPipeline
{
    // Step 2 — extract value and unit from JSON payload (path-lite) or bare string
    public static (string? Value, string? Unit) Extract(string payload, string? valueField, string? unitField)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var value = valueField is not null ? WalkPath(doc.RootElement, valueField) : null;
            var unit = unitField is not null ? WalkPath(doc.RootElement, unitField) : null;
            return (value ?? payload.Trim(), unit);
        }
        catch (JsonException)
        {
            return (payload.Trim(), null);
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
        return double.TryParse(valueStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d.ToString($"F{precision}", CultureInfo.InvariantCulture)
            : valueStr;
    }

    // Step 5 — fill format template with {value}, {unit}, {label} tokens
    public static string ComposeLabel(string? template, string? value, string? unit, string? staticLabel)
    {
        if (template is null) return staticLabel ?? value ?? string.Empty;
        return template
            .Replace("{value}", value ?? string.Empty)
            .Replace("{unit}", unit ?? string.Empty)
            .Replace("{label}", staticLabel ?? string.Empty);
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
