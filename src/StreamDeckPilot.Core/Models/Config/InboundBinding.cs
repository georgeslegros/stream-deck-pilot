namespace StreamDeckPilot.Core.Models.Config;

public record InboundBinding(
    string Topic,
    string? ValueField,
    string? UnitField,
    bool ExpectsRetained,
    TimeSpan? StalenessTimeout);
