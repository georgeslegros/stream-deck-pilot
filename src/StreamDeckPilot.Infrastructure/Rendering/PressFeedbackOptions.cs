namespace StreamDeckPilot.Infrastructure.Rendering;

/// <summary>Press feedback (#22), bound from the <c>PressFeedback</c> configuration section.</summary>
public record PressFeedbackOptions
{
    /// <summary>Show a pending look on a key whose press publishes, until its state comes back.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How long a key bound to an inbound topic waits for a new value after a press before it
    /// flashes red. Nothing arriving in time usually means a broken automation or an offline device.
    /// </summary>
    public double TimeoutSeconds { get; init; } = 3;

    /// <summary>
    /// How long the red flash lasts. Also how long the pending look shows on a key with no inbound
    /// topic, which has no state that could confirm the press.
    /// </summary>
    public int FlashMilliseconds { get; init; } = 600;
}
