namespace StreamDeckPilot.Core.Models.Config;

public record DeviceConfig(
    int SchemaVersion,
    string Serial,
    IReadOnlyList<Page> Pages);
