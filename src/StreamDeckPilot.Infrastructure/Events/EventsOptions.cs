namespace StreamDeckPilot.Infrastructure.Events;

/// <summary>Live events (#108). Section <c>Events</c>.</summary>
public sealed class EventsOptions
{
    public const string Section = "Events";

    /// <summary>
    /// Coalesced events (KeyRendered per key, ValueReceived per topic) are delivered at most this
    /// often per key; in between, the latest one waits and replaces any older one. Default 4.
    /// </summary>
    public int MaxPerSecondPerKey { get; set; } = 4;

    /// <summary>
    /// Events buffered per subscriber. A subscriber that falls this far behind loses events and is
    /// told to resynchronise from a fresh snapshot instead of slowing anyone down. Default 1024.
    /// </summary>
    public int SubscriberCapacity { get; set; } = 1024;

    /// <summary>Recent events kept for SSE resume (<c>Last-Event-ID</c>). Default 1024.</summary>
    public int ReplayCapacity { get; set; } = 1024;

    /// <summary>Seconds between SSE keep-alive comments on an idle stream. Default 15.</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>MQTT payloads longer than this are cut in ValueReceived events. Default 1024 characters.</summary>
    public int MaxValueLength { get; set; } = 1024;

    public string? Error() =>
        MaxPerSecondPerKey is < 1 or > 1000 ? "Events:MaxPerSecondPerKey must be between 1 and 1000." :
        SubscriberCapacity is < 16 or > 100_000 ? "Events:SubscriberCapacity must be between 16 and 100000." :
        ReplayCapacity is < 0 or > 100_000 ? "Events:ReplayCapacity must be between 0 and 100000." :
        HeartbeatSeconds is < 1 or > 300 ? "Events:HeartbeatSeconds must be between 1 and 300." :
        MaxValueLength is < 16 or > 1_000_000 ? "Events:MaxValueLength must be between 16 and 1000000." :
        null;
}
