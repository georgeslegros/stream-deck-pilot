namespace StreamDeckPilot.Infrastructure.Sparkline;

public sealed class SparklineOptions
{
    /// <summary>
    /// Points kept per button. The window is split into this many buckets; a value arriving in
    /// the same bucket as the previous one replaces it, so a chatty topic never needs more.
    /// </summary>
    public int MaxPoints { get; set; } = 240;
}
