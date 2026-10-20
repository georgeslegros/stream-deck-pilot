using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamDeckPilot.Core.Models.Config;

// How a rule's key blinks (#23, #131). In JSON a rule's "blink" is either true (blink with the
// global Blink:* defaults) or an object overriding any of them:
//   { "style": "flash" | "pulse", "strength": 0.1–0.9, "periodMs": 400–10000 }
// Style: flash switches hard between the normal and the dim look; pulse fades smoothly between them.
// Strength: the fraction of brightness kept in the dimmest frame. PeriodMs: one full cycle.
// Every property is optional (null = the global default); a spec with none set writes back as true,
// so existing configs with "blink": true round-trip unchanged.
[JsonConverter(typeof(BlinkSpecJsonConverter))]
public sealed record BlinkSpec(string? Style = null, double? Strength = null, int? PeriodMs = null)
{
    public const string Flash = "flash";
    public const string Pulse = "pulse";
    public static IReadOnlyList<string> Styles { get; } = [Flash, Pulse];

    public const double MinStrength = 0.1;
    public const double MaxStrength = 0.9;
    public const int MinPeriodMs = 400;
    public const int MaxPeriodMs = 10_000;

    /// <summary>"blink": true — every setting from the global defaults.</summary>
    public static BlinkSpec On { get; } = new();

    public bool IsDefaults => Style is null && Strength is null && PeriodMs is null;
}

// true → BlinkSpec.On, false → null (no blink), object → BlinkSpec. Writes true when no property
// is overridden, otherwise an object with only the properties that are set.
public sealed class BlinkSpecJsonConverter : JsonConverter<BlinkSpec>
{
    // Same options as everything else, so property matching behaves like the rest of the config.
    private sealed record Wire(string? Style = null, double? Strength = null, int? PeriodMs = null);

    public override BlinkSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return BlinkSpec.On;
            case JsonTokenType.False:
                return null;
            case JsonTokenType.StartObject:
                var wire = JsonSerializer.Deserialize<Wire>(ref reader, options)!;
                return new BlinkSpec(wire.Style, wire.Strength, wire.PeriodMs);
            default:
                throw new JsonException("blink must be true, false or an object { style, strength, periodMs }.");
        }
    }

    public override void Write(Utf8JsonWriter writer, BlinkSpec value, JsonSerializerOptions options)
    {
        if (value.IsDefaults)
        {
            writer.WriteBooleanValue(true);
            return;
        }
        writer.WriteStartObject();
        if (value.Style is not null) writer.WriteString(Name("Style", options), value.Style);
        if (value.Strength is { } strength) writer.WriteNumber(Name("Strength", options), strength);
        if (value.PeriodMs is { } period) writer.WriteNumber(Name("PeriodMs", options), period);
        writer.WriteEndObject();
    }

    private static string Name(string property, JsonSerializerOptions options) =>
        options.PropertyNamingPolicy?.ConvertName(property) ?? property;
}
