namespace StreamDeckPilot.Core.Models.Config;

public record ButtonDefinition(
    string ButtonId,
    int KeyIndex,
    string PageId,
    DisplaySpec Display,
    InboundBinding? Inbound,
    IReadOnlyList<ConditionalRule> Rules,
    IReadOnlyDictionary<string, IReadOnlyList<ButtonAction>> Gestures);

/// <summary>
/// Gesture names accepted as keys of <see cref="ButtonDefinition.Gestures"/>. Names are exact
/// (case-sensitive): the press handler looks them up as written. LongPress/DoublePress are reserved
/// by the spec but not handled yet, so the validator rejects them until they are.
/// </summary>
public static class GestureNames
{
    public const string Tap = "Tap";

    public static IReadOnlyList<string> Supported { get; } = [Tap];
}
