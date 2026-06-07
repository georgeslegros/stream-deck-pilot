using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Tests.Migration;

public sealed class StoreMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public StoreMigrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private IOptions<StorageOptions> Opts =>
        Options.Create(new StorageOptions { BaseDirectory = _dir });

    [Fact]
    public async Task CatalogueStore_V1File_LoadsSuccessfully()
    {
        // At v1 with no migrations registered, a v1 file is an identity pass-through
        var cataloguePath = Path.Combine(_dir, "catalog.json");
        var v1 = new { schemaVersion = 1, devices = Array.Empty<object>() };
        await File.WriteAllTextAsync(cataloguePath,
            JsonSerializer.Serialize(v1, JsonOptions.Default));

        var store = new CatalogueStore(Opts, new MigrationRunner([]));
        var catalogue = await store.LoadAsync();

        Assert.Equal(1, catalogue.SchemaVersion);
        Assert.Empty(catalogue.Devices);

        // File on disk unchanged
        var onDisk = await File.ReadAllTextAsync(cataloguePath);
        Assert.Contains("\"schemaVersion\": 1", onDisk);
    }

    [Fact]
    public async Task CatalogueStore_BelowFloor_Throws()
    {
        var cataloguePath = Path.Combine(_dir, "catalog.json");
        var v0 = new { schemaVersion = 0, devices = Array.Empty<object>() };
        await File.WriteAllTextAsync(cataloguePath,
            JsonSerializer.Serialize(v0, JsonOptions.Default));

        var store = new CatalogueStore(Opts, new MigrationRunner([]));
        await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task ConfigStore_SaveAlwaysWritesCurrentVersion()
    {
        var store = new ConfigStore(Opts, new MigrationRunner([]));
        var config = new Core.Models.Config.DeviceConfig(99, "SN1",
            [new Core.Models.Config.ButtonGridPage("main", [])]);

        await store.SaveAsync(config);
        var loaded = await store.LoadAsync("SN1");

        // Version normalised to current (2), not the 99 passed in
        Assert.Equal(2, loaded!.SchemaVersion);
    }

    [Fact]
    public async Task ConfigStore_RoundTrip_IdentityAtCurrentVersion()
    {
        var store = new ConfigStore(Opts, new MigrationRunner([]));
        var config = new Core.Models.Config.DeviceConfig(1, "SN2",
            [new Core.Models.Config.ButtonGridPage("main", [])]);

        await store.SaveAsync(config);
        var loaded = await store.LoadAsync("SN2");

        Assert.NotNull(loaded);
        Assert.Equal("SN2", loaded.Serial);
        Assert.Single(loaded.Pages);
    }

    private sealed class LambdaMigration(int fromVersion, Func<JsonObject, JsonObject> fn) : IMigration
    {
        public int FromVersion => fromVersion;
        public JsonObject Apply(JsonObject doc) => fn(doc);
    }
}
