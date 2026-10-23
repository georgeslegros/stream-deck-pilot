using System.Text.Json;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Infrastructure.Events;

namespace StreamDeckPilot.Api.Events;

/// <summary>Wire format of live events: camelCase JSON on one line, the concrete event's fields included.</summary>
public static class LiveEventJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonOptions.Default) { WriteIndented = false };

    public static string Serialize(LiveEvent e) => JsonSerializer.Serialize(e, e.GetType(), Options);

    public static JsonElement ToElement(LiveEvent e) => JsonSerializer.SerializeToElement(e, e.GetType(), Options);

    public static string Serialize(LiveSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    /// <summary>SSE / resume id: <c>{bootId}:{sequence}</c>.</summary>
    public static string EventId(string bootId, long sequence) => $"{bootId}:{sequence}";

    /// <summary>The sequence in a resume id from this run of the service, or null (absent, malformed, or from an earlier run).</summary>
    public static long? ParseEventId(string? id, string bootId)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var colon = id.LastIndexOf(':');
        if (colon <= 0 || !string.Equals(id[..colon], bootId, StringComparison.Ordinal)) return null;
        return long.TryParse(id[(colon + 1)..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var seq) ? seq : null;
    }
}
