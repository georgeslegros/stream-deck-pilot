using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using StreamDeckPilot.Core;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Mqtt.Pipeline;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;

// Renders a device config to Stream-Deck-style board PNG(s) using the real renderer.
// Usage: StreamDeckPreview <config.json> [outDir] [sampleData.json] [cols] [rows]
//   sampleData.json: { "<buttonId>": { "value": "850", "unit": "ppm", "label": "22/18" }, ... }
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: StreamDeckPreview <config.json> [outDir] [sampleData.json] [cols] [rows]");
    return 1;
}

var configPath = args[0];
var outDir = args.Length > 1 ? args[1] : ".";
var sampleDataPath = args.Length > 2 ? args[2] : null;
var cols = args.Length > 3 ? int.Parse(args[3]) : 5;
var rows = args.Length > 4 ? int.Parse(args[4]) : 3;
var scale = args.Length > 5 ? int.Parse(args[5]) : 3;   // px-per-tile-px; bump for a zoomed close-up
Directory.CreateDirectory(outDir);

// Load the config and migrate it to the current schema (handles v1 files).
var node = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
node = new MigrationRunner([new ConfigV1ToV2Migration()])
    .Migrate(node, SchemaVersions.ConfigMinimumSupported, SchemaVersions.ConfigCurrentVersion, configPath);
var config = node.Deserialize<DeviceConfig>(JsonOptions.Default)!;

// Optional live-data overlay: buttonId -> {value, unit, label}.
var sample = new Dictionary<string, SampleDatum>(StringComparer.Ordinal);
if (sampleDataPath is not null)
    foreach (var (k, v) in JsonNode.Parse(File.ReadAllText(sampleDataPath))!.AsObject())
        sample[k] = new SampleDatum(
            v?["value"]?.GetValue<string>(), v?["unit"]?.GetValue<string>(), v?["label"]?.GetValue<string>());

// Composer wired to read custom icons from the config's storage base (when the file follows
// the <base>/config/<serial>.json layout); MDI built-ins need no storage.
var baseDir = Directory.GetParent(configPath)?.Parent?.FullName ?? Path.GetTempPath();
var composer = new KeyBitmapComposer(new IconResolver(
    new CustomImageSource(Options.Create(new StorageOptions { BaseDirectory = baseDir })),
    NullLogger<IconResolver>.Instance));

ButtonRenderState BuildState(ButtonDefinition b, SampleDatum? s)
{
    var hasData = s is not null && (s.Value is not null || s.Unit is not null || s.Label is not null);
    var formatted = InboundPipeline.FormatValue(s?.Value);
    var (colour, icon) = hasData ? InboundPipeline.EvaluateRules(s!.Value, b.Rules) : (null, null);
    var center = InboundPipeline.ResolveZone(b.Display.Center, hasData, formatted, s?.Unit, s?.Label);
    var bottom = InboundPipeline.ResolveZone(b.Display.Bottom, hasData, formatted, s?.Unit, s?.Label);
    return new ButtonRenderState(b.ButtonId, colour, icon ?? b.Display.BaseIcon,
        b.Display.IconPlacement, center, bottom);
}

foreach (var page in config.Pages.OfType<ButtonGridPage>())
{
    // Same board layout as the service's live preview (BoardPreviewRenderer); keys with no button are black.
    var byKey = page.Buttons.Where(b => b.KeyIndex >= 0 && b.KeyIndex < cols * rows)
        .ToDictionary(b => b.KeyIndex);
    using var board = BoardPreviewRenderer.Compose(cols, rows, scale, key =>
        byKey.TryGetValue(key, out var b)
            ? composer.ComposeImage(BuildState(b, sample.GetValueOrDefault(b.ButtonId)), config.Serial)
            : null);

    var outPath = Path.Combine(outDir, $"board-{page.PageId}.png");
    board.SaveAsPng(outPath);
    Console.WriteLine(outPath);
}

return 0;

internal sealed record SampleDatum(string? Value, string? Unit, string? Label);
