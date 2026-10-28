using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Web.Auth;

namespace StreamDeckPilot.Tests.Web;

/// <summary>
/// Sign-in for the web UI (#97): with the UI on, every page needs a signed-in user; local accounts,
/// first-run setup, rate limiting, roles, the cross-site WebSocket guard, sign-out and sessions that
/// survive a restart.
/// </summary>
public sealed class AuthTests
{
    // ── Pages need a session ─────────────────────────────────────────────────

    [Fact]
    public async Task SignedOut_PagesRedirectToSignIn_TheCircuitIs401_AssetsArePublic()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();

        var home = await browser.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/account/signin", home.Headers.Location!.AbsolutePath);
        Assert.Contains("returnUrl=%2F", home.Headers.Location.Query);

        var board = await browser.Http.GetAsync("/board/SN001");
        Assert.Contains("returnUrl=%2Fboard%2FSN001", board.Headers.Location!.Query);

        // Not a page: no redirect, a plain 401.
        using var plain = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync("/_framework/blazor.web.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync("/_content/StreamDeckPilot.Web/app.css")).StatusCode);

        var signIn = await browser.GetHtmlAsync("account/signin");
        Assert.Contains("<base href=\"/\"", signIn);
        Assert.Contains("data-testid=\"signin-username\"", signIn);
        Assert.Contains("name=\"Input.UserName\"", signIn);
    }

    // ── Local accounts ───────────────────────────────────────────────────────

    [Fact]
    public async Task LocalSignIn_RightPassword_GoesBackToTheReturnUrl_WithASecureSessionCookie()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient("https://localhost/");

        var response = await browser.SignInAsync(WebAuthFactory.ViewerUser, WebAuthFactory.ViewerPassword,
            "account/signin?returnUrl=%2Fboard%2FSN001");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/board/SN001", response.Headers.Location!.AbsolutePath);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AuthExtensions.CookieName + "="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Theory]
    [InlineData(WebAuthFactory.ViewerUser, "wrong password 123")]
    [InlineData("nobody", WebAuthFactory.ViewerPassword)]
    public async Task LocalSignIn_WrongCredentials_StaysSignedOut_AndPublishesSignInFailed(string user, string password)
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        using var events = factory.Services.GetRequiredService<IEventBus>().Subscribe();
        var browser = factory.CreateFormClient();

        var response = await browser.SignInAsync(user, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Wrong user name or password", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies)
                     && cookies.Any(c => c.StartsWith(AuthExtensions.CookieName + "=")));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.Http.GetAsync("/")).StatusCode);

        var failed = await NextAsync<SignInFailed>(events);
        Assert.Equal("local", failed.Method);
        Assert.Equal("invalid_credentials", failed.Reason);
    }

    [Fact]
    public async Task LocalSignIn_IsRateLimitedPerAddress_EvenWithTheRightPassword()
    {
        await using var factory = new WebAuthFactory(new() { ["Auth:AttemptLimit"] = "3" });
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync(WebAuthFactory.AdminUser, "wrong password 123")).StatusCode);

        var limited = await browser.SignInAsync(WebAuthFactory.AdminUser, WebAuthFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("Too many attempts", await limited.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.Http.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task SignIn_WithoutTheAntiforgeryToken_IsRefused()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.GetHtmlAsync("account/signin");

        var response = await browser.Http.PostAsync("account/signin", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "signin",
            ["Input.UserName"] = WebAuthFactory.AdminUser,
            ["Input.Password"] = WebAuthFactory.AdminPassword,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.Http.GetAsync("/")).StatusCode);
    }

    // ── Roles ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Policies_ViewerReads_AdminDoesEverything_NoRoleNothing()
    {
        await using var factory = new WebAuthFactory();
        _ = factory.Server;
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();

        async Task<bool> Allowed(System.Security.Claims.ClaimsPrincipal user, string policy) =>
            (await authorization.AuthorizeAsync(user, policy)).Succeeded;

        var admin = AuthRoles.Principal("oidc:p:a", "a", AuthRoles.Admin, "oidc:p", DateTimeOffset.UtcNow);
        var viewer = AuthRoles.Principal("oidc:p:v", "v", AuthRoles.Viewer, "oidc:p", DateTimeOffset.UtcNow);
        var noRole = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "x")], "oidc"));

        Assert.True(await Allowed(admin, AuthRoles.ViewerPolicy));
        Assert.True(await Allowed(admin, AuthRoles.AdminPolicy));
        Assert.True(await Allowed(viewer, AuthRoles.ViewerPolicy));
        Assert.False(await Allowed(viewer, AuthRoles.AdminPolicy));
        Assert.False(await Allowed(noRole, AuthRoles.ViewerPolicy));
    }

    // ── Cross-site WebSocket hijacking ───────────────────────────────────────

    [Fact]
    public async Task CircuitAndHub_RefuseAnotherOrigin_EvenWithASession()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.AdminUser, WebAuthFactory.AdminPassword);

        async Task<HttpStatusCode> Negotiate(string path, string? origin, bool apiKey = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{path}/negotiate?negotiateVersion=1");
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (apiKey) request.Headers.Add("X-Api-Key", WebAuthFactory.ApiKey);
            return (await browser.Http.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Negotiate("/_blazor", "http://localhost"));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate("/_blazor", "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate("/_blazor", "http://localhost.evil.example"));
        // The hub takes the session too (#97), from the app's own origin only.
        Assert.Equal(HttpStatusCode.OK, await Negotiate("/hubs/live", "http://localhost"));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate("/hubs/live", "http://evil.example"));
        // No Origin: a script, not a browser acting for a user.
        Assert.Equal(HttpStatusCode.OK, await Negotiate("/hubs/live", null, apiKey: true));
    }

    [Fact]
    public async Task PublicOrigin_IsTheOnlyOriginAccepted()
    {
        await using var factory = new WebAuthFactory(new() { ["Web:PublicOrigin"] = "https://deck.example.com" });
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.AdminUser, WebAuthFactory.AdminPassword);

        async Task<HttpStatusCode> Negotiate(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
            request.Headers.Add("Origin", origin);
            return (await browser.Http.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Negotiate("https://deck.example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate("http://localhost"));
    }

    [Fact]
    public async Task EventStream_TakesASession_OrTheKey_AndNothingElse()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/events")).StatusCode);

        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.ViewerUser, WebAuthFactory.ViewerPassword);
        await using var sse = await StreamDeckPilot.Tests.Api.SseClient.OpenAsync(browser.Http, "/api/events");
        Assert.Equal("snapshot", (await sse.NextAsync()).Event);

        // The REST API itself still needs the key (#98 adds sessions there).
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/devices")).StatusCode);
    }

    // ── Sign-out ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignOut_IsAFormPostWithAnAntiforgeryToken_AndEndsTheSession()
    {
        await using var factory = new WebAuthFactory();
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.AdminUser, WebAuthFactory.AdminPassword);

        // Without a token (what another site could send): refused, still signed in.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.Http.PostAsync("account/signout", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/")).StatusCode);

        var denied = await browser.GetHtmlAsync("account/denied");   // a page with the sign-out form
        var response = await browser.Http.PostAsync("account/signout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = FormClient.Token(denied),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/signin", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.Http.GetAsync("/")).StatusCode);
    }

    // ── Sessions survive a restart ───────────────────────────────────────────

    [Fact]
    public async Task Session_SurvivesARestart_KeysAreOnTheDataVolume()
    {
        var storage = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            string cookie;
            await using (var first = new WebAuthFactory(storageDirectory: storage))
            {
                await first.SeedUsersAsync();
                var browser = first.CreateFormClient();
                var signedIn = await browser.SignInAsync(WebAuthFactory.AdminUser, WebAuthFactory.AdminPassword);
                cookie = signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthExtensions.CookieName + "=")).Split(';')[0];
            }

            Assert.NotEmpty(Directory.GetFiles(Path.Combine(storage, "dataprotection-keys"), "key-*.xml"));

            await using var second = new WebAuthFactory(storageDirectory: storage);
            using var client = second.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("Cookie", cookie);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }
        finally
        {
            if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
        }
    }

    // ── Startup checks ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Auth:Local:Enabled", "false", "no way to sign in")]
    [InlineData("Auth:Providers:0:Name", "keycloak", "Web:PublicOrigin is required")]
    [InlineData("Web:PublicOrigin", "https://deck.example.com/deck", "scheme and host only")]
    public async Task BadSettings_StopStartup(string key, string value, string message)
    {
        var settings = new Dictionary<string, string?> { [key] = value };
        if (key.StartsWith("Auth:Providers"))
        {
            settings["Auth:Providers:0:Authority"] = "https://id.example.com/realms/home";
            settings["Auth:Providers:0:ClientId"] = "stream-deck-pilot";
        }
        await using var factory = new WebAuthFactory(settings);
        var error = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains(message, error.ToString());
    }

    [Theory]
    [InlineData(null, "", "/")]
    [InlineData("/board/SN1?x=1", "", "/board/SN1?x=1")]
    [InlineData("https://evil.example/", "", "/")]
    [InlineData("//evil.example/", "", "/")]
    [InlineData("/\\evil.example", "", "/")]
    [InlineData("/deck/board/SN1", "/deck", "/deck/board/SN1")]
    [InlineData("/other-app/", "/deck", "/deck/")]
    public void ReturnUrl_StaysInsideTheApp(string? returnUrl, string pathBase, string expected) =>
        Assert.Equal(expected, AuthExtensions.SafeReturnUrl(returnUrl, pathBase));

    private static async Task<T> NextAsync<T>(EventSubscription events) where T : LiveEvent
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var e in events.Reader.ReadAllAsync(timeout.Token))
            if (e is T match) return match;
        throw new TimeoutException();
    }
}
