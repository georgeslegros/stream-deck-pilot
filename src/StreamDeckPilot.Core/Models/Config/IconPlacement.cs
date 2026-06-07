namespace StreamDeckPilot.Core.Models.Config;

// How a tile's icon is rendered. User-chosen — never inferred from the data the tile carries.
// Corner is the default (value 0) so an omitted field never collides with centred text.
public enum IconPlacement
{
    Corner,   // small accent, top-left
    Center,   // large, centred — the hero (only when no Center text is present)
}
