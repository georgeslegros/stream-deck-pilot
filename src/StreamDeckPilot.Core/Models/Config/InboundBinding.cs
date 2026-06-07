namespace StreamDeckPilot.Core.Models.Config;

public record InboundBinding(
    string Topic,
    string? ValueField,
    string? UnitField,
    bool ExpectsRetained,
    TimeSpan? StalenessTimeout,
    string? LabelField = null);   // MQTT field for a live caption/label (e.g. "22.5/18.0")
