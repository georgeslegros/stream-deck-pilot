namespace StreamDeckPilot.Core.Models.Config;

// Condition grammar: ">N", ">=N", "<N", "<=N", "==N", "between:A:B"
public record ConditionalRule(
    string Condition,
    string? BackgroundColour,
    string? Icon);
