namespace StreamDeckPilot.Core.Models.Catalogue;

public record DeviceEntry(
    string Serial,
    string Model,
    int KeyRows,
    int KeyColumns,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen)
{
    public int KeyCount => KeyRows * KeyColumns;
}
