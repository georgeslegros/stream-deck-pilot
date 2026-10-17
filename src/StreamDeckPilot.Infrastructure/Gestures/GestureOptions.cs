namespace StreamDeckPilot.Infrastructure.Gestures;

/// <summary>Timings for <c>LongPress</c> and <c>DoubleTap</c>, bound from the <c>Gestures</c> section.</summary>
public record GestureOptions
{
    /// <summary>How long a key must be held before its <c>LongPress</c> fires (while still held).</summary>
    public int LongPressMs { get; init; } = 500;

    /// <summary>
    /// How long after a release a second press still counts as a <c>DoubleTap</c>. Only keys that
    /// define <c>DoubleTap</c> wait this long before their <c>Tap</c> runs.
    /// </summary>
    public int DoubleTapMs { get; init; } = 300;
}
