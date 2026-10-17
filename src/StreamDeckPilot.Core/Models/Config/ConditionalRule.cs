using System.Text.Json.Serialization;

namespace StreamDeckPilot.Core.Models.Config;

// Condition grammar (see Rules/RuleCondition): numeric ">N", ">=N", "<N", "<=N", "==N",
// "between:A:B"; text "eq:X", "ne:X", "in:A,B,C" (trimmed, case-insensitive); "any".
// Blink (#23): while this rule is the first match, the key blinks to draw attention. Optional and
// omitted from the JSON when false, so existing configs read and write back unchanged.
public record ConditionalRule(
    string Condition,
    string? BackgroundColour,
    string? Icon,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Blink = false);
