namespace StreamDeckPilot.Infrastructure.Sparkline;

public sealed class SparklineOptions
{
    /// <summary>
    /// Points kept per button. The window is split into this many buckets; a value arriving in
    /// the same bucket as the previous one replaces it, so a chatty topic never needs more.
    /// </summary>
    public int MaxPoints { get; set; } = 240;

    /// <summary>
    /// How often the history is saved to the data volume (#144), in seconds; it is also saved on
    /// shutdown. Only written when something changed. 0 saves on shutdown only.
    /// </summary>
    public int SaveIntervalSeconds { get; set; } = 300;
}
