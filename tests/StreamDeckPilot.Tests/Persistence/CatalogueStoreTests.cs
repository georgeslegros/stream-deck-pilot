using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Tests.Persistence;

public sealed class CatalogueStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public CatalogueStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private CatalogueStore Store() => new(Options.Create(new StorageOptions { BaseDirectory = _dir }));

    [Fact]
    public async Task AppendDevice_IsAnUpsert_RetriesNeverDuplicate()
    {
        // The supervisor retries a failed append on every scan (#68), and a retry can follow a write
        // that actually succeeded: the same serial must stay a single entry, with LastSeen advanced
        // and FirstSeen kept.
        var store = Store();
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = first.AddMinutes(5);

        await store.AppendDeviceAsync(new DeviceEntry("SN1", "Stream Deck", 3, 5, first, first));
        await store.AppendDeviceAsync(new DeviceEntry("SN1", "Stream Deck", 3, 5, later, later));
        await store.AppendDeviceAsync(new DeviceEntry("SN1", "Stream Deck", 3, 5, later, later));

        var entry = Assert.Single((await Store().LoadAsync()).Devices);
        Assert.Equal("SN1", entry.Serial);
        Assert.Equal(first, entry.FirstSeen);
        Assert.Equal(later, entry.LastSeen);
    }
}
