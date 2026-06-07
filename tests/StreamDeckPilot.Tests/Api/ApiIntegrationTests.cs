using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Tests.Api;

public sealed class ApiIntegrationTests : IAsyncDisposable
{
    private readonly StreamDeckApiFactory _factory = new();

    async ValueTask IAsyncDisposable.DisposeAsync() =>
        await ((IAsyncDisposable)_factory).DisposeAsync();

    // --- Auth ---

    [Fact]
    public async Task NoApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");
        var response = await client.GetAsync("/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_ExemptFromAuth()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- GET /devices ---

    [Fact]
    public async Task GetDevices_EmptyCatalogue_ReturnsEmptyArray()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/devices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal("[]", json.Trim());
    }

    [Fact]
    public async Task GetDevices_WithSeededDevice_ReturnsIt()
    {
        await _factory.SeedDeviceAsync("SN001");
        var client = _factory.CreateAuthenticatedClient();
        var doc = await client.GetFromJsonAsync<JsonElement[]>("/devices");
        Assert.NotNull(doc);
        Assert.Single(doc);
        Assert.Equal("SN001", doc[0].GetProperty("serial").GetString());
        Assert.Equal("Unknown", doc[0].GetProperty("connectionState").GetString());
    }

    // --- GET /devices/{serial}/status ---

    [Fact]
    public async Task GetDeviceStatus_UnknownSerial_Returns404()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/devices/GHOST/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDeviceStatus_KnownSerial_ReturnsUnknown()
    {
        await _factory.SeedDeviceAsync("SN002");
        var client = _factory.CreateAuthenticatedClient();
        var doc = await client.GetFromJsonAsync<JsonElement>("/devices/SN002/status");
        Assert.Equal("Unknown", doc.GetProperty("connectionState").GetString());
    }

    // --- PUT /devices/{serial}/config ---

    private static StringContent ValidConfigBody(string serial) =>
        new(JsonSerializer.Serialize(
            new DeviceConfig(1, serial, [new ButtonGridPage("main", [])]),
            JsonOptions.Default),
            Encoding.UTF8, "application/json");

    [Fact]
    public async Task PutConfig_UnknownSerial_Returns400()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.PutAsync("/devices/GHOST/config", ValidConfigBody("GHOST"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("catalogue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutConfig_ValidConfig_Returns204AndPersists()
    {
        await _factory.SeedDeviceAsync("SN003");
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.PutAsync("/devices/SN003/config", ValidConfigBody("SN003"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify persisted
        var get = await client.GetFromJsonAsync<JsonElement>("/devices/SN003/config");
        Assert.Equal("SN003", get.GetProperty("serial").GetString());
    }

    [Fact]
    public async Task PutConfig_KeyIndexOutOfRange_Returns400WithError()
    {
        await _factory.SeedDeviceAsync("SN004");
        var client = _factory.CreateAuthenticatedClient();
        var config = new DeviceConfig(1, "SN004", [
            new ButtonGridPage("main", [
                new("b1", 99, "main", new(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>())
            ])
        ]);
        var body = new StringContent(JsonSerializer.Serialize(config, JsonOptions.Default),
            Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/devices/SN004/config", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("out of range", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutConfig_DuplicatePosition_Returns400()
    {
        await _factory.SeedDeviceAsync("SN005");
        var client = _factory.CreateAuthenticatedClient();
        var config = new DeviceConfig(1, "SN005", [
            new ButtonGridPage("main", [
                new("b1", 0, "main", new(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>()),
                new("b2", 0, "main", new(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>())
            ])
        ]);
        var body = new StringContent(JsonSerializer.Serialize(config, JsonOptions.Default),
            Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/devices/SN005/config", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("duplicate", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutConfig_BrokenNavTarget_Returns400()
    {
        await _factory.SeedDeviceAsync("SN006");
        var client = _factory.CreateAuthenticatedClient();
        var config = new DeviceConfig(1, "SN006", [
            new ButtonGridPage("main", [
                new("b1", 0, "main", new(), null, [],
                    new Dictionary<string, IReadOnlyList<ButtonAction>>
                    {
                        ["Tap"] = [new NavigateAction("ghost-page")]
                    })
            ])
        ]);
        var body = new StringContent(JsonSerializer.Serialize(config, JsonOptions.Default),
            Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/devices/SN006/config", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ghost-page", await response.Content.ReadAsStringAsync());
    }

    // --- Navigation troubleshooting endpoints ---

    private static StringContent TwoPageConfigBody(string serial) =>
        new(JsonSerializer.Serialize(
            new DeviceConfig(1, serial, [
                new ButtonGridPage("main", [
                    new("m0", 0, "main", new(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>())
                ]),
                new ButtonGridPage("second", [
                    new("s0", 0, "second", new(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>())
                ]),
            ]),
            JsonOptions.Default),
            Encoding.UTF8, "application/json");

    [Fact]
    public async Task Navigate_ValidPage_SetsActivePage()
    {
        await _factory.SeedDeviceAsync("SN010");
        var client = _factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsync("/devices/SN010/config", TwoPageConfigBody("SN010"))).StatusCode);

        var nav = await client.PostAsJsonAsync("/devices/SN010/navigate", new { pageId = "second" });
        Assert.Equal(HttpStatusCode.OK, nav.StatusCode);
        var navBody = await nav.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("second", navBody.GetProperty("activePageId").GetString());
        Assert.False(navBody.GetProperty("rendered").GetBoolean()); // no device connected in tests

        // The active page now reflects the navigation.
        var active = await client.GetFromJsonAsync<JsonElement>("/devices/SN010/active-page");
        Assert.Equal("second", active.GetProperty("activePageId").GetString());
        var pages = active.GetProperty("availablePages").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("main", pages);
        Assert.Contains("second", pages);
    }

    [Fact]
    public async Task Navigate_UnknownPage_Returns400WithAvailablePages()
    {
        await _factory.SeedDeviceAsync("SN011");
        var client = _factory.CreateAuthenticatedClient();
        await client.PutAsync("/devices/SN011/config", TwoPageConfigBody("SN011"));

        var nav = await client.PostAsJsonAsync("/devices/SN011/navigate", new { pageId = "does-not-exist" });
        Assert.Equal(HttpStatusCode.BadRequest, nav.StatusCode);
        var json = await nav.Content.ReadAsStringAsync();
        Assert.Contains("does-not-exist", json);
        Assert.Contains("main", json); // available pages echoed back
    }

    [Fact]
    public async Task Navigate_NoConfig_Returns404()
    {
        await _factory.SeedDeviceAsync("SN012"); // catalogue only, no config
        var client = _factory.CreateAuthenticatedClient();

        var nav = await client.PostAsJsonAsync("/devices/SN012/navigate", new { pageId = "main" });
        Assert.Equal(HttpStatusCode.NotFound, nav.StatusCode);
    }
}
