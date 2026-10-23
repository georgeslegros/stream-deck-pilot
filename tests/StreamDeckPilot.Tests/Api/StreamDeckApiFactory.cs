using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Tests.Api;

public sealed class StreamDeckApiFactory(Dictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>, IAsyncDisposable
{
    public const string TestApiKey = "test-api-key-12345";
    public string StorageDir { get; } = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public StreamDeckApiFactory() : this(null) { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting as well, so settings Program reads while building services (e.g. Web:Enabled)
        // are already there; ConfigureAppConfiguration only applies once the app is built.
        foreach (var (key, value) in settings ?? [])
            builder.UseSetting(key, value);
        if (string.Equals(settings?.GetValueOrDefault("Web:Enabled"), "true", StringComparison.OrdinalIgnoreCase))
            builder.UseStaticWebAssets();
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiKey"] = TestApiKey,
                ["Storage:BaseDirectory"] = StorageDir,
            });
            if (settings is not null)
                config.AddInMemoryCollection(settings);
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        return client;
    }

    public async Task SeedDeviceAsync(string serial = "SN001")
    {
        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<CatalogueStore>();
        await store.AppendDeviceAsync(new(
            serial, "Stream Deck MK.2", 3, 5,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(StorageDir))
            Directory.Delete(StorageDir, recursive: true);
    }
}
