using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Core.Rendering;

public sealed record ButtonRenderState(
    string ButtonId,
    string? BackgroundColour,   // "#RRGGBB" or null → black
    string? IconReference,      // "builtin:x" or "custom:x" or null
    IconPlacement IconPlacement,// where the icon renders (user-chosen, never inferred)
    string? CenterText,         // large hero text, centred (when present)
    string? BottomText,         // small caption along the bottom
    bool IsDimmed = false);
