using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Core.Models.Config;
using static StreamDeckPilot.Core.SchemaVersions;

namespace StreamDeckPilot.Infrastructure.Persistence;

public class ConfigStore(IOptions<StorageOptions> options, MigrationRunner? migration = null)
{
    private readonly MigrationRunner _migration = migration ?? new MigrationRunner([]);

    private string ConfigPath(string serial) =>
        Path.Combine(options.Value.BaseDirectory, "config", $"{serial}.json");

    public async Task<DeviceConfig?> LoadAsync(string serial)
    {
        var path = ConfigPath(serial);
        if (!File.Exists(path)) return null;

        await using var stream = File.OpenRead(path);
        var node = await JsonNode.ParseAsync(stream)
                   ?? throw new InvalidOperationException($"Config file for {serial} is empty or null.");
        var doc = node.AsObject();

        // Throws UnsupportedSchemaVersionException if file is below the support floor
        doc = _migration.Migrate(doc, ConfigMinimumSupported, ConfigCurrentVersion, path);

        return doc.Deserialize<DeviceConfig>(JsonOptions.Default);
    }

    public async Task SaveAsync(DeviceConfig config)
    {
        // Always persist at the current version
        var toSave = config with { SchemaVersion = ConfigCurrentVersion };
        await AtomicFileWriter.WriteJsonAsync(ConfigPath(config.Serial), toSave, JsonOptions.Default);
    }

    public Task<IReadOnlyList<string>> ListSerialsAsync()
    {
        var dir = Path.Combine(options.Value.BaseDirectory, "config");
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>([]);

        var serials = Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(s => s is not null)
            .Cast<string>()
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(serials);
    }
}
