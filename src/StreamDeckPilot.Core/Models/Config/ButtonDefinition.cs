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
/// (case-sensitive): the press handler looks them up as written. A button may define any subset;
/// defining <see cref="LongPress"/> or <see cref="DoubleTap"/> also changes when that key's
/// <see cref="Tap"/> runs (on release, or after the double-tap window) — see GestureDetector.
/// </summary>
public static class GestureNames
{
    public const string Tap = "Tap";
    public const string LongPress = "LongPress";
    public const string DoubleTap = "DoubleTap";

    public static IReadOnlyList<string> Supported { get; } = [Tap, LongPress, DoubleTap];
}
