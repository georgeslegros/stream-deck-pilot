using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Core.Rendering;

public sealed record ButtonRenderState(
    string ButtonId,
    string? BackgroundColour,   // "#RRGGBB" or null → black
    string? IconReference,      // "builtin:x" or "custom:x" or null
    IconPlacement IconPlacement,// where the icon renders (user-chosen, never inferred)
    string? CenterText,         // large hero text, centred (when present)
    string? BottomText,         // small caption along the bottom
    bool IsDimmed = false,
    bool DependsOnBroker = false, // MQTT-bound or publishes on tap: affected by a broker outage (#26)
    bool IsOffline = false,       // render-time only: set on a copy while painting, never stored
    BlinkSpec? Blink = null,      // the matching rule asks for attention (#23, #131); null = no blink
    float BlinkLevel = 1f,        // render-time only: brightness kept in this blink frame (1 = normal look)
    SparklineSeries? Sparkline = null, // trend drawn behind the text (#25), null when not opted in
    PressFeedback Feedback = PressFeedback.None) // render-time only, like IsOffline (#22)
{
    /// <summary>True when the key should actually blink: stale and broker-offline keys don't.</summary>
    public bool BlinkActive => Blink is not null && !IsDimmed && !IsOffline;

    /// <summary>
    /// True when the button needs the broker: its value comes from MQTT, or a tap publishes.
    /// Navigate-only and static keys still work locally while the broker is down.
    /// </summary>
    public static bool ButtonDependsOnBroker(ButtonDefinition button) =>
        button.Inbound is not null
        || button.Gestures?.Values.Any(actions => actions?.Any(a => a is PublishAction) == true) == true;
}

/// <summary>Press feedback drawn over a key (#22): waiting for the key's state, or no answer came back.</summary>
public enum PressFeedback
{
    None,
    Pending,
    Failed,
}
