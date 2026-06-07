using System.Text.Json.Nodes;

namespace StreamDeckPilot.Core.Migration;

// v1 → v2: the flat DisplaySpec fields become positional text zones + explicit icon placement.
//   formatTemplate → center.template   (the live hero value)
//   staticLabel    → bottom.label      (the static caption)
//   baseIcon       → baseIcon          (unchanged)
//   iconPlacement  → "corner" when the tile was a sensor (had a formatTemplate),
//                    "center" otherwise — this reproduces the v1 renderer's behaviour exactly.
public sealed class ConfigV1ToV2Migration : IMigration
{
    public int FromVersion => 1;

    public JsonObject Apply(JsonObject doc)
    {
        if (doc["pages"] is JsonArray pages)
            foreach (var page in pages)
                if (page is JsonObject pageObj && pageObj["buttons"] is JsonArray buttons)
                    foreach (var button in buttons)
                        if (button is JsonObject buttonObj && buttonObj["display"] is JsonObject display)
                            MigrateDisplay(display);

        return doc;
    }

    private static void MigrateDisplay(JsonObject display)
    {
        var formatTemplate = Take(display, "formatTemplate");
        var staticLabel = Take(display, "staticLabel");

        // Preserve the v1 look: sensors drew the icon in the corner, everything else centred it.
        display["iconPlacement"] = formatTemplate is not null ? "corner" : "center";

        if (formatTemplate is not null)
            display["center"] = new JsonObject { ["label"] = null, ["template"] = formatTemplate };

        if (staticLabel is not null)
            display["bottom"] = new JsonObject { ["label"] = staticLabel, ["template"] = null };
    }

    // Removes a property and returns its string value (null if absent or JSON null).
    private static string? Take(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node)) return null;
        obj.Remove(name);
        return node?.GetValue<string>();
    }
}
