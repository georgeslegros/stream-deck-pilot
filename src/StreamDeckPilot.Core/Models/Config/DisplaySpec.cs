namespace StreamDeckPilot.Core.Models.Config;

// How a tile looks. Layout is driven by user choices (which zone holds what, where the
// icon sits), not inferred from the kind of data the tile carries:
//   Center → large hero text;  Bottom → small caption;  icon → per IconPlacement.
// Precision is the number of decimals {value} shows for numbers (0–3); null keeps the
// default: whole numbers without a decimal, other numbers with one.
// Sparkline, when set, draws a faint trend of the recent numeric values behind the text.
public record DisplaySpec(
    string? BaseIcon = null,
    IconPlacement IconPlacement = IconPlacement.Corner,
    TextZone? Center = null,
    TextZone? Bottom = null,
    int? Precision = null,
    SparklineSpec? Sparkline = null)
{
    public const int MaxPrecision = 3;
}
