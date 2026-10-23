using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using StreamDeckPilot.Tests.Api;

namespace StreamDeckPilot.Tests.Web;

/// <summary>
/// The web UI (#96) is off by default and, when on, must not change the REST API: only UI endpoints
/// (pages, the Blazor circuit, static assets) skip the API key; pages and the circuit need a
/// signed-in user instead (#97, see AuthTests).
/// </summary>
public sealed class WebUiHostingTests
{
    private const string Key = StreamDeckApiFactory.TestApiKey;

    // ── Off (default) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_ByDefault_NoUiIsMapped_AndTheApiIsUnchanged()
    {
        await using var factory = new StreamDeckApiFactory();
        using var anonymous = factory.CreateClient();
        using var keyed = factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.NotFound, (await keyed.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await keyed.GetAsync("/_framework/blazor.web.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await keyed.GetAsync("/_content/MudBlazor/MudBlazor.min.css")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }

    // ── On ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enabled_ServesTheShellToASignedInUser_WithoutTheApiKey()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.ViewerUser, WebAuthFactory.ViewerPassword);

        var home = await browser.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("text/html", home.Content.Headers.ContentType?.MediaType);
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("Stream Deck Pilot", html);
        Assert.Contains("<base href=\"/\"", html);

        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/board/SN001")).StatusCode);
        // Static assets need neither the key nor a session (the sign-in page uses them).
        using var anonymous = factory.CreateClient();
        var script = await anonymous.GetAsync("/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.True((await script.Content.ReadAsByteArrayAsync()).Length > 10_000, "blazor.web.js should have a body");
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/_content/MudBlazor/MudBlazor.min.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/_content/StreamDeckPilot.Web/app.css")).StatusCode);

        // The Blazor circuit hub: negotiation works with the session, without the key.
        var negotiate = await browser.Http.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
    }

    [Fact]
    public async Task Enabled_TheApiStillNeedsItsKey()
    {
        await using var factory = new WebUiApiFactory();
        using var anonymous = factory.CreateClient();
        using var keyed = factory.CreateClient();
        keyed.DefaultRequestHeaders.Add("X-Api-Key", Key);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/devices/SN001/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/config/upgrade", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/openapi.json")).StatusCode);

        // Unknown paths behave as before: no key → 401, with the key → 404.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/no-such-page")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await keyed.GetAsync("/no-such-page")).StatusCode);
    }

    private sealed class WebUiApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _storage = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Static web assets come from the build output, as in Development (a published app has
            // them in wwwroot instead); without this they'd be served empty.
            builder.UseStaticWebAssets();
            // UseSetting (not ConfigureAppConfiguration) so Program sees it while building services.
            builder.UseSetting("Web:Enabled", "true");
            builder.UseSetting("ApiKey", Key);
            builder.UseSetting("Storage:BaseDirectory", _storage);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
        }
    }
}
