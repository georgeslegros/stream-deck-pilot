namespace StreamDeckPilot.Core.Models.Config;

// Condition grammar (see Rules/RuleCondition): numeric ">N", ">=N", "<N", "<=N", "==N",
// "between:A:B"; text "eq:X", "ne:X", "in:A,B,C" (trimmed, case-insensitive); "any".
public record ConditionalRule(
    string Condition,
    string? BackgroundColour,
    string? Icon);
