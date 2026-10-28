using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamDeckPilot.Infrastructure.Security;
using StreamDeckPilot.Tests.Web;

namespace StreamDeckPilot.Tests.Api;

/// <summary>
/// Named, revocable API keys (#98) and signed-in sessions on the REST API, end to end through the
/// middleware: scopes, revocation and expiry without a restart (including open streams), the
/// bootstrap key switch, and the CSRF guard for browser sessions.
/// </summary>
public sealed class ApiKeysApiTests
{
    private static async Task<CreatedApiKey> CreateKeyAsync(WebAuthFactory factory, ApiKeyScope scope,
        DateTimeOffset? expires = null, string name = "script")
    {
        var result = await factory.Services.GetRequiredService<ApiKeyStore>().CreateAsync(name, scope, expires, "admin");
        Assert.Null(result.Error);
        return result.Created!;
    }

    private static HttpClient Client(WebAuthFactory factory, string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var e) ? e.GetString() : null;

    private static HubConnection Hub(WebAuthFactory factory, string key) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/live"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;   // the test server has no WebSockets
                o.Headers["X-Api-Key"] = key;
            })
            .Build();

    private static async Task AssertStreamEndsAsync(SseClient sse)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try { await sse.NextAsync(TimeSpan.FromSeconds(10)); }
            catch (EndOfStreamException) { return; }
            catch (IOException) { return; }
        }
        Assert.Fail("The event stream didn't end.");
    }

    // ── Keys ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NamedKey_Works_UntilRevoked_ThenGetsTheSame401AsAnUnknownKey()
    {
        await using var factory = new WebAuthFactory();
        var key = await CreateKeyAsync(factory, ApiKeyScope.Full);
        var client = Client(factory, key.Key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/devices")).StatusCode);

        Assert.True(await factory.Services.GetRequiredService<ApiKeyStore>().RevokeAsync(key.Record.Id, "admin"));

        var revoked = await client.GetAsync("/devices");
        var unknown = await Client(factory, "sdp_0123456789ab_" + new string('A', 43)).GetAsync("/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(Problem(await unknown.Content.ReadAsStringAsync()), Problem(await revoked.Content.ReadAsStringAsync()));

        static string Problem(string json)
        {
            var p = JsonDocument.Parse(json).RootElement;   // everything but the per-request traceId
            return $"{p.GetProperty("status")}|{p.GetProperty("detail")}|{p.GetProperty("error")}";
        }
    }

    [Fact]
    public async Task ReadOnlyKey_CanRead_AndUseTheLiveEndpoints_ButNotWrite()
    {
        await using var factory = new WebAuthFactory();
        var key = await CreateKeyAsync(factory, ApiKeyScope.ReadOnly);
        var client = Client(factory, key.Key);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/devices")).StatusCode);
        foreach (var write in new[]
                 {
                     new HttpRequestMessage(HttpMethod.Post, "/devices/SN1/force-render"),
                     new HttpRequestMessage(HttpMethod.Post, "/devices/SN1/navigate") { Content = JsonContent.Create(new { pageId = "x" }) },
                     new HttpRequestMessage(HttpMethod.Put, "/devices/SN1/config") { Content = JsonContent.Create(new { }) },
                     new HttpRequestMessage(HttpMethod.Delete, "/devices/SN1/images/a.png"),
                 })
        {
            var response = await client.SendAsync(write);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("read_only_key", await ErrorCodeAsync(response));
        }

        await using (var sse = await SseClient.OpenAsync(client, "/api/events"))
            Assert.Equal("snapshot", (await sse.NextAsync()).Event);

        await using var hub = Hub(factory, key.Key);
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("Subscribe", (string[]?)null, (string?)null);
    }

    [Fact]
    public async Task FullKey_CanWrite()
    {
        await using var factory = new WebAuthFactory();
        var key = await CreateKeyAsync(factory, ApiKeyScope.Full);
        // Past the middleware: the unknown deck is the endpoint's 404, not a 401 or 403.
        Assert.Equal(HttpStatusCode.NotFound, (await Client(factory, key.Key).PostAsync("/devices/SN1/force-render", null)).StatusCode);
    }

    [Fact]
    public async Task NamedKey_IsNeverTakenFromTheQueryString()
    {
        await using var factory = new WebAuthFactory();
        var key = await CreateKeyAsync(factory, ApiKeyScope.Full);
        var anonymous = factory.CreateClient();
        foreach (var url in new[] { $"/devices?X-Api-Key={key.Key}", $"/devices?access_token={key.Key}", $"/api/events?access_token={key.Key}" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task BootstrapKey_CanBeDisabled_AndStartupSaysSetupCantRun()
    {
        await using var factory = new WebAuthFactory(new() { ["Auth:BootstrapKeyEnabled"] = "false" });
        var key = await CreateKeyAsync(factory, ApiKeyScope.Full);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(factory, WebAuthFactory.ApiKey).GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, key.Key).GetAsync("/devices")).StatusCode);
        Assert.Contains(factory.Logs.Lines, l => l.Contains("first-run setup needs the bootstrap API key") && l.Contains("Auth:BootstrapKeyEnabled"));
        Assert.Contains(factory.Logs.Lines, l => l.Contains("bootstrap API key is disabled"));
    }

    // ── Revocation and expiry end open connections ──────────────────────────

    [Fact]
    public async Task Revoking_EndsTheKeysOpenEventStream()
    {
        await using var factory = new WebAuthFactory();
        var key = await CreateKeyAsync(factory, ApiKeyScope.ReadOnly);
        await using var sse = await SseClient.OpenAsync(Client(factory, key.Key), "/api/events");
        Assert.Equal("snapshot", (await sse.NextAsync()).Event);

        await factory.Services.GetRequiredService<ApiKeyStore>().RevokeAsync(key.Record.Id, "admin");
        await AssertStreamEndsAsync(sse);
    }

    [Fact]
    public async Task Revoking_AbortsTheKeysHubConnection()
    {
        await using var factory = new WebAuthFactory();
        var key = await CreateKeyAsync(factory, ApiKeyScope.Full);
        await using var hub = Hub(factory, key.Key);
        var closed = new TaskCompletionSource();
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("Subscribe", (string[]?)null, (string?)null);

        await factory.Services.GetRequiredService<ApiKeyStore>().RevokeAsync(key.Record.Id, "admin");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HubConnectionState.Disconnected, hub.State);
    }

    [Fact]
    public async Task Revoking_AbortsTheKeysWebSocketHubConnection()
    {
        // Over a real socket: the WebSocket transport has no next request to refuse, so the hub
        // itself must abort the connection.
        await using var factory = new WebAuthFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        var baseUrl = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        var key = await CreateKeyAsync(factory, ApiKeyScope.Full);

        await using var hub = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/live", o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.Headers["X-Api-Key"] = key.Key;
            })
            .Build();
        var closed = new TaskCompletionSource();
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("Subscribe", (string[]?)null, (string?)null);

        await factory.Services.GetRequiredService<ApiKeyStore>().RevokeAsync(key.Record.Id, "admin");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Expiry_EndsTheKeysOpenEventStream_AndLaterRequests()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WebAuthFactory(testServices: s => s.AddSingleton<TimeProvider>(clock));
        var key = await CreateKeyAsync(factory, ApiKeyScope.ReadOnly, expires: clock.GetUtcNow().AddHours(1));
        var client = Client(factory, key.Key);
        await using var sse = await SseClient.OpenAsync(client, "/api/events");
        Assert.Equal("snapshot", (await sse.NextAsync()).Event);

        clock.Advance(TimeSpan.FromHours(1));
        await AssertStreamEndsAsync(sse);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/devices")).StatusCode);
    }

    // ── Key management ──────────────────────────────────────────────────────

    [Fact]
    public async Task KeyManagement_NeedsAnAdminSession_NoKeyCanMintOrRevokeKeys()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        var full = await CreateKeyAsync(factory, ApiKeyScope.Full);

        // The only key management is the admin page; there is no key-management API.
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? "").ToList();
        Assert.DoesNotContain(routes, r => (r.Contains("api-key", StringComparison.OrdinalIgnoreCase) || r.Contains("apikey", StringComparison.OrdinalIgnoreCase))
                                           && r != "/admin/api-keys");

        // With a key (even the bootstrap key) the page isn't served.
        foreach (var key in new[] { full.Key, WebAuthFactory.ApiKey })
        {
            var response = await Client(factory, key).GetAsync("/admin/api-keys");
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var viewer = factory.CreateFormClient();
        await viewer.SignInAsync(WebAuthFactory.ViewerUser, WebAuthFactory.ViewerPassword);
        var denied = await viewer.Http.GetAsync("/admin/api-keys");
        Assert.True(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, $"{denied.StatusCode}");
        if (denied.StatusCode == HttpStatusCode.Redirect) Assert.Contains("/account/denied", denied.Headers.Location!.ToString());

        var admin = factory.CreateFormClient();
        await admin.SignInAsync(WebAuthFactory.AdminUser, WebAuthFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync("/admin/api-keys")).StatusCode);
    }

    // ── Sessions on the REST API ────────────────────────────────────────────

    [Fact]
    public async Task AdminSession_CanRead_AndWriteWithTheCsrfHeaderFromThisOrigin()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.AdminUser, WebAuthFactory.AdminPassword);
        var http = browser.Http;

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/devices")).StatusCode);

        var noHeader = await http.PostAsync("/devices/SN1/force-render", null);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        Assert.Equal("csrf_header_required", await ErrorCodeAsync(noHeader));

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var response = await http.SendAsync(new HttpRequestMessage(method, "/devices/SN1/config"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("csrf_header_required", await ErrorCodeAsync(response));
        }

        var crossSite = new HttpRequestMessage(HttpMethod.Post, "/devices/SN1/force-render");
        crossSite.Headers.Add("X-Requested-With", "XMLHttpRequest");
        crossSite.Headers.Add("Origin", "https://evil.example.com");
        var refused = await http.SendAsync(crossSite);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("cross_origin", await ErrorCodeAsync(refused));

        var sameSite = new HttpRequestMessage(HttpMethod.Post, "/devices/SN1/force-render");
        sameSite.Headers.Add("X-Requested-With", "XMLHttpRequest");
        sameSite.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(sameSite)).StatusCode);   // the endpoint's own answer
    }

    [Fact]
    public async Task ViewerSession_IsReadOnly_OnTheApi()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.ViewerUser, WebAuthFactory.ViewerPassword);

        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/devices")).StatusCode);
        var write = new HttpRequestMessage(HttpMethod.Post, "/devices/SN1/force-render");
        write.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var response = await browser.Http.SendAsync(write);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("read_only_session", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task NoSession_AndNoKey_IsStill401()
    {
        await using var factory = new WebAuthFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/openapi.json")).StatusCode);
    }
}
