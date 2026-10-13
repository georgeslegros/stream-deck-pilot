namespace StreamDeckPilot.Infrastructure.Brightness;

public record BrightnessOptions
{
    /// <summary>Inbound topic. Payload: integer 0–100, or <c>{"value": n}</c>. Publish it retained.</summary>
    public string SetTopic { get; init; } = "home/streamdeck/brightness/set";

    /// <summary>Outbound topic carrying the brightness actually applied (retained).</summary>
    public string StateTopic { get; init; } = "home/streamdeck/brightness/state";

    /// <summary>
    /// How long a key press lights up a deck whose brightness is 0 before it goes dark again.
    /// Each further press during that window restarts it.
    /// </summary>
    public int WakeTimeoutSeconds { get; init; } = 60;
}
