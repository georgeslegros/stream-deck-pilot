using System.Text.Json.Serialization;

namespace StreamDeckPilot.Core.Models.Config;

// How a tile looks. Layout is driven by user choices (which zone holds what, where the
// icon sits), not inferred from the kind of data the tile carries:
//   Center → large hero text;  Bottom → small caption;  icon → per IconPlacement.
// Precision is the number of decimals {value} shows for numbers (0–3); null keeps the
// default: whole numbers without a decimal, other numbers with one.
// Sparkline, when set, draws a faint trend of the recent numeric values behind the text.
// PressFeedback overrides the press feedback for this key (#22): "auto" | "ring" | "off";
// null means "auto". PressFeedbackTimeoutSeconds overrides the global wait for the key's
// state (null = global). These three are left out of the JSON when unset, so existing
// configs round-trip unchanged.
public record DisplaySpec(
    string? BaseIcon = null,
    IconPlacement IconPlacement = IconPlacement.Corner,
    TextZone? Center = null,
    TextZone? Bottom = null,
    int? Precision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SparklineSpec? Sparkline = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PressFeedback = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PressFeedbackTimeoutSeconds = null)
{
    public const int MaxPrecision = 3;
    public const double MinPressFeedbackTimeoutSeconds = 1;
    public const double MaxPressFeedbackTimeoutSeconds = 120;
}

/// <summary>Values of <see cref="DisplaySpec.PressFeedback"/> (#22).</summary>
public static class PressFeedbackModes
{
    /// <summary>Ring until the key's state comes back; red flash if it doesn't in time.</summary>
    public const string Auto = "auto";
    /// <summary>Ring until the key's state comes back or the timeout passes; never red.</summary>
    public const string Ring = "ring";
    /// <summary>No feedback on this key.</summary>
    public const string Off = "off";

    public static IReadOnlyList<string> Supported { get; } = [Auto, Ring, Off];
}
