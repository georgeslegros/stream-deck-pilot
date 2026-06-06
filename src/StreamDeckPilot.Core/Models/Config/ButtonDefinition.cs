namespace StreamDeckPilot.Core.Models.Config;

public record ButtonDefinition(
    string ButtonId,
    int KeyIndex,
    string PageId,
    DisplaySpec Display,
    InboundBinding? Inbound,
    IReadOnlyList<ConditionalRule> Rules,
    IReadOnlyDictionary<string, IReadOnlyList<ButtonAction>> Gestures);
