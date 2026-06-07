namespace StreamDeckPilot.Core.Models.Config;

// A tile text slot. Template is resolved against live MQTT data ({value}/{unit}/{label}).
// Label is static, rendered as-is. When both are set, Label is the fallback used while
// there is no live data to resolve the template against.
public record TextZone(
    string? Label,
    string? Template);
