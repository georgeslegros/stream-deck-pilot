using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Core.Models.Catalogue;
using static StreamDeckPilot.Core.SchemaVersions;

namespace StreamDeckPilot.Infrastructure.Persistence;

public class CatalogueStore(IOptions<StorageOptions> options, MigrationRunner? migration = null)
{
    private readonly string _path = Path.Combine(options.Value.BaseDirectory, "catalog.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly MigrationRunner _migration = migration ?? new MigrationRunner([]);

    public async Task<DeviceCatalogue> LoadAsync()
    {
        if (!File.Exists(_path))
            return new DeviceCatalogue(CatalogueCurrentVersion, []);

        await using var stream = File.OpenRead(_path);
        var node = await JsonNode.ParseAsync(stream)
                   ?? throw new InvalidOperationException($"catalog.json is empty or null.");
        var doc = node.AsObject();

        // Throws UnsupportedSchemaVersionException if file is below the support floor
        doc = _migration.Migrate(doc, CatalogueMinimumSupported, CatalogueCurrentVersion, _path);

        return doc.Deserialize<DeviceCatalogue>(JsonOptions.Default)
               ?? new DeviceCatalogue(CatalogueCurrentVersion, []);
    }

    public async Task AppendDeviceAsync(DeviceEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var catalogue = await LoadAsync();
            var existing = catalogue.Devices.FirstOrDefault(d => d.Serial == entry.Serial);

            List<DeviceEntry> updated;
            if (existing is null)
                updated = [..catalogue.Devices, entry];
            else
                updated = catalogue.Devices
                    .Select(d => d.Serial == entry.Serial ? d with { LastSeen = entry.LastSeen } : d)
                    .ToList();

            // Always write at the current version
            var next = catalogue with { Devices = updated, SchemaVersion = CatalogueCurrentVersion };
            await AtomicFileWriter.WriteJsonAsync(_path, next, JsonOptions.Default);
        }
        finally
        {
            _lock.Release();
        }
    }
}
