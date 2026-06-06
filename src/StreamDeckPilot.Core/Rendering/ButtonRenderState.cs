namespace StreamDeckPilot.Core.Rendering;

public record ButtonRenderState(
    string ButtonId,
    string? BackgroundColour,  // "#RRGGBB" or null → black
    string? IconReference,     // "builtin:x" or "custom:x" or null (Plan 06)
    string? LabelText,
    bool IsDimmed,
    bool IsSensor = false);    // true → Layout A (live numeric value is the hero)
