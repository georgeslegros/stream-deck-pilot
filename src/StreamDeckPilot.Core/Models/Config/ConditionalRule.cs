using System.Text.Json.Serialization;

namespace StreamDeckPilot.Core.Models.Config;

// Condition grammar (see Rules/RuleCondition): numeric ">N", ">=N", "<N", "<=N", "==N",
// "between:A:B"; text "eq:X", "ne:X", "in:A,B,C" (trimmed, case-insensitive); "any".
// Blink (#23, #131): while this rule is the first match, the key blinks to draw attention. JSON true
// blinks with the global Blink:* defaults; an object overrides them (see BlinkSpec). Optional and
// omitted from the JSON when off, so existing configs read and write back unchanged.
public record ConditionalRule(
    string Condition,
    string? BackgroundColour,
    string? Icon,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BlinkSpec? Blink = null);
