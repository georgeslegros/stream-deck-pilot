using System.Text.Json.Serialization;

namespace StreamDeckPilot.Core.Models.Config;

public record DeviceConfig(
    int SchemaVersion,
    string Serial,
    IReadOnlyList<Page> Pages,
    // Optional per-device override of Navigation:HomeAfterInactivitySeconds (#24): null uses the
    // global option, 0 turns it off for this deck. Omitted from the JSON when unset, so existing
    // configs round-trip unchanged and no schema bump is needed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? HomeAfterInactivitySeconds = null);
