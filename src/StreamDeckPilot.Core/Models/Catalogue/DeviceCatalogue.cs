namespace StreamDeckPilot.Core.Models.Catalogue;

public record DeviceCatalogue(
    int SchemaVersion,
    IReadOnlyList<DeviceEntry> Devices);
