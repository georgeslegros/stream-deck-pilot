namespace StreamDeckPilot.Infrastructure.Rendering;

public record BlinkOptions
{
    /// <summary>Floor for <see cref="IntervalMilliseconds"/>, so a typo can't flood the USB link with repaints.</summary>
    public const int MinIntervalMilliseconds = 200;

    /// <summary>How long each half of a blink lasts (#23). Clamped to at least <see cref="MinIntervalMilliseconds"/>.</summary>
    public int IntervalMilliseconds { get; init; } = 500;
}
