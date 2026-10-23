namespace StreamDeckPilot.Infrastructure.Events;

/// <summary>
/// Which events a subscriber wants. Null <see cref="Types"/> = every type. With a
/// <see cref="Serial"/>, per-device events for other decks are skipped; service-wide events
/// (broker, brightness, MQTT values) always pass.
/// </summary>
public sealed record EventFilter(IReadOnlySet<string>? Types = null, string? Serial = null)
{
    public static EventFilter All { get; } = new();

    public bool Matches(LiveEvent e) =>
        (Types is null || Types.Contains(e.Type)) &&
        (Serial is null || e.Serial is null || string.Equals(e.Serial, Serial, StringComparison.Ordinal));

    /// <summary>
    /// Builds a filter from a comma-separated type list (case-insensitive) and an optional serial.
    /// Returns an error message for an unknown type name.
    /// </summary>
    public static EventFilter Parse(string? types, string? serial, out string? error)
    {
        error = null;
        HashSet<string>? set = null;
        if (!string.IsNullOrWhiteSpace(types))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = LiveEventTypes.Canonical(raw);
                if (name is null)
                {
                    error = $"Unknown event type '{raw}'. Known types: {string.Join(", ", LiveEventTypes.All)}.";
                    return All;
                }
                set.Add(name);
            }
        }
        return new EventFilter(set, string.IsNullOrWhiteSpace(serial) ? null : serial);
    }
}
