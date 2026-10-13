using System.Globalization;

namespace StreamDeckPilot.Core.Rules;

/// <summary>
/// A parsed <c>ConditionalRule.Condition</c>. Grammar:
/// <list type="bullet">
///   <item>Numeric: <c>&gt;N</c>, <c>&gt;=N</c>, <c>&lt;N</c>, <c>&lt;=N</c>, <c>==N</c>, <c>between:A:B</c> (inclusive).
///   Only match when the value parses as a number.</item>
///   <item>Text: <c>eq:TEXT</c>, <c>ne:TEXT</c>, <c>in:A,B,C</c>. Compare the value as text, trimmed and
///   case-insensitive. A number is compared as the text it arrived as (<c>eq:1</c> does not match <c>1.0</c>).</item>
///   <item><c>any</c>: matches every value (an "otherwise" rule at the end of the list).</item>
/// </list>
/// </summary>
public abstract record RuleCondition
{
    public abstract bool Matches(string? value);

    public static bool TryParse(string? condition, out RuleCondition? parsed, out string? error)
    {
        parsed = null;
        error = null;
        var c = condition?.Trim() ?? string.Empty;
        if (c.Length == 0) { error = "condition is empty"; return false; }

        if (c.Equals("any", StringComparison.OrdinalIgnoreCase)) { parsed = new AnyCondition(); return true; }

        if (c.StartsWith("eq:", StringComparison.OrdinalIgnoreCase)) return TryText(c[3..], negate: false, out parsed, out error);
        if (c.StartsWith("ne:", StringComparison.OrdinalIgnoreCase)) return TryText(c[3..], negate: true, out parsed, out error);
        if (c.StartsWith("in:", StringComparison.OrdinalIgnoreCase))
        {
            var items = c[3..].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (items.Length == 0) { error = "'in:' needs at least one value, e.g. 'in:heat,cool'"; return false; }
            parsed = new TextInCondition(items);
            return true;
        }

        if (c.StartsWith("between:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = c.Split(':');
            if (parts.Length != 3 || !TryNum(parts[1], out var lo) || !TryNum(parts[2], out var hi))
            {
                error = "expected 'between:A:B' with numeric A and B";
                return false;
            }
            if (lo > hi) { error = $"'between:{parts[1]}:{parts[2]}' can never match (A > B)"; return false; }
            parsed = new NumericCondition(v => v >= lo && v <= hi);
            return true;
        }

        // Longest operators first so ">=" is not read as ">".
        foreach (var (op, make) in NumericOperators)
        {
            if (!c.StartsWith(op, StringComparison.Ordinal)) continue;
            var operand = c[op.Length..];
            if (!TryNum(operand, out var n))
            {
                error = op == "==" ? $"'{c}' is not numeric — use 'eq:{operand.Trim()}' to match text" : $"'{c}' needs a number after '{op}'";
                return false;
            }
            parsed = new NumericCondition(make(n));
            return true;
        }

        error = $"unknown condition '{c}' — expected >N, >=N, <N, <=N, ==N, between:A:B, eq:X, ne:X, in:A,B or any";
        return false;
    }

    private static readonly (string Op, Func<double, Func<double, bool>> Make)[] NumericOperators =
    [
        (">=", n => v => v >= n),
        ("<=", n => v => v <= n),
        ("==", n => v => v == n),
        (">", n => v => v > n),
        ("<", n => v => v < n),
    ];

    private static bool TryText(string operand, bool negate, out RuleCondition? parsed, out string? error)
    {
        var text = operand.Trim();
        parsed = null;
        error = null;
        if (text.Length == 0) { error = $"'{(negate ? "ne" : "eq")}:' needs a value, e.g. 'eq:on'"; return false; }
        parsed = new TextEqualsCondition(text, negate);
        return true;
    }

    private static bool TryNum(string s, out double n) =>
        double.TryParse(s.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out n);

    // Same leniency as the pipeline has always used for incoming values.
    internal static bool TryValueAsNumber(string? value, out double n)
    {
        n = 0;
        return value is not null && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out n);
    }

    private static bool TextEquals(string? value, string expected) =>
        value is not null && string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private sealed record AnyCondition : RuleCondition
    {
        public override bool Matches(string? value) => value is not null;
    }

    private sealed record NumericCondition(Func<double, bool> Test) : RuleCondition
    {
        public override bool Matches(string? value) => TryValueAsNumber(value, out var n) && Test(n);
    }

    private sealed record TextEqualsCondition(string Expected, bool Negate) : RuleCondition
    {
        public override bool Matches(string? value) => value is not null && TextEquals(value, Expected) != Negate;
    }

    private sealed record TextInCondition(string[] Items) : RuleCondition
    {
        public override bool Matches(string? value) => Items.Any(i => TextEquals(value, i));
    }
}
