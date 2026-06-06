using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Tests.Api;

public sealed class StreamDeckApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    public const string TestApiKey = "test-api-key-12345";
    public string StorageDir { get; } = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiKey"] = TestApiKey,
                ["Storage:BaseDirectory"] = StorageDir,
            });
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
