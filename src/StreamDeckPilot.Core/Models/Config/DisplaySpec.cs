namespace StreamDeckPilot.Core.Models.Config;

// How a tile looks. Layout is driven by user choices (which zone holds what, where the
// icon sits), not inferred from the kind of data the tile carries:
//   Center → large hero text;  Bottom → small caption;  icon → per IconPlacement.
public record DisplaySpec(
    string? BaseIcon = null,
    IconPlacement IconPlacement = IconPlacement.Corner,
    TextZone? Center = null,
    TextZone? Bottom = null);
