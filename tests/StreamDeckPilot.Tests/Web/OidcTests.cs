using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Web.Auth;

namespace StreamDeckPilot.Tests.Web;

/// <summary>
/// Signing in with an OpenID Connect provider (#97), end to end against a local fake provider:
/// authorization code with PKCE, the redirect URI built from Web:PublicOrigin, role mapping.
/// </summary>
public sealed class OidcTests : IAsyncLifetime
{
    private FakeOidcProvider _provider = null!;

    public async ValueTask InitializeAsync() => _provider = await FakeOidcProvider.StartAsync();
    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    private WebAuthFactory App(string? pathBase = null, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Web:PublicOrigin"] = "https://localhost",
            ["Auth:Providers:0:Name"] = "test",
            ["Auth:Providers:0:DisplayName"] = "Test ID",
            ["Auth:Providers:0:Authority"] = _provider.Authority,
            ["Auth:Providers:0:ClientId"] = FakeOidcProvider.ClientId,
            ["Auth:Providers:0:ClientSecret"] = FakeOidcProvider.ClientSecret,
            ["Auth:Providers:0:RequireHttpsMetadata"] = "false",
            ["Proxy:PathBase"] = pathBase,
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new WebAuthFactory(settings);
    }

    /// <summary>Follows the browser through the provider and back; returns the app's final response.</summary>
    private async Task<HttpResponseMessage> SignInAsync(FormClient browser, string start)
    {
        var challenge = await browser.Http.GetAsync(start);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.StartsWith(_provider.Authority + "/authorize", challenge.Headers.Location!.ToString());

        using var toProvider = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var back = await toProvider.GetAsync(challenge.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);

        // The provider sends the browser to the redirect URI on the public origin, which is the test
        // server's address. The correlation and nonce cookies are Secure, as on a real HTTPS deployment.
        return await browser.Http.GetAsync(back.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task SignInPage_ShowsOneButtonPerProvider_AndNoLocalFormWhenLocalIsAuto()
    {
        await using var app = App();
        var html = await app.CreateFormClient("https://localhost/").GetHtmlAsync("account/signin");
        Assert.Contains("Sign in with Test ID", html);
        Assert.Contains("data-testid=\"provider-test\"", html);
        Assert.DoesNotContain("signin-username", html);   // local accounts are off by default once a provider exists
    }

    [Fact]
    public async Task CodeFlow_SignsTheUserIn_AsTheMappedRole_AndReturnsToTheirPage()
    {
        await using var app = App();
        _provider.ExtraClaims = new() { ["roles"] = new[] { "viewer", "other" } };
        var browser = app.CreateFormClient("https://localhost/");

        var done = await SignInAsync(browser, "account/signin/oidc/test?returnUrl=%2Fboard%2FSN001");

        Assert.Equal("https://localhost/signin-oidc-test", _provider.LastRedirectUri);
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal("/board/SN001", done.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task RedirectUri_ComesFromThePublicOrigin_NotTheRequestHost_AndCarriesThePathBase()
    {
        await using var app = App(pathBase: "/deck");
        var browser = app.CreateFormClient("https://localhost/");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/deck/account/signin/oidc/test");
        request.Headers.Host = "attacker.example";
        var challenge = await browser.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        Assert.Equal("https://localhost/deck/signin-oidc-test", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task UserWithoutAMappedRole_IsRefused_AndSignInFailedIsPublished()
    {
        await using var app = App();
        _provider.ExtraClaims = new() { ["roles"] = new[] { "guest" } };
        using var events = app.Services.GetRequiredService<IEventBus>().Subscribe();
        var browser = app.CreateFormClient("https://localhost/");

        var done = await SignInAsync(browser, "account/signin/oidc/test");

        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal("/account/signin?error=no_role", done.Headers.Location!.OriginalString);
        Assert.Contains("has no access", await browser.GetHtmlAsync("account/signin?error=no_role"));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.Http.GetAsync("/")).StatusCode);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var e in events.Reader.ReadAllAsync(timeout.Token))
            if (e is SignInFailed failed)
            {
                Assert.Equal("oidc:test", failed.Method);
                Assert.Equal("no_role", failed.Reason);
                break;
            }
    }

    [Fact]
    public async Task DefaultRole_LetsEveryProviderUserView()
    {
        await using var app = App(extra: new() { ["Auth:Providers:0:DefaultRole"] = "viewer" });
        _provider.ExtraClaims = new();
        var browser = app.CreateFormClient("https://localhost/");

        var done = await SignInAsync(browser, "account/signin/oidc/test");

        Assert.True("/" == done.Headers.Location!.OriginalString, string.Join("\n", app.Logs.Lines.Where(l => l.Contains("Sign-in"))));
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task UnknownProvider_Is404()
    {
        await using var app = App();
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateFormClient("https://localhost/").Http.GetAsync("account/signin/oidc/nope")).StatusCode);
    }

    // ── Role mapping ─────────────────────────────────────────────────────────

    private static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "oidc"));

    [Fact]
    public void Roles_AdminWinsOverViewer_ValuesCompareCaseInsensitively()
    {
        var provider = new OidcProviderOptions { Name = "p", RoleClaim = "groups", AdminValues = ["deck-admins"], ViewerValues = ["family"] };
        Assert.Equal(AuthRoles.Admin, OidcClaims.Role(provider, User(new Claim("groups", "family"), new Claim("groups", "Deck-Admins"))));
        Assert.Equal(AuthRoles.Viewer, OidcClaims.Role(provider, User(new Claim("groups", "family"))));
        Assert.Null(OidcClaims.Role(provider, User(new Claim("groups", "guests"))));
        Assert.Null(OidcClaims.Role(provider, User(new Claim("roles", "deck-admins"))));   // another claim
    }

    [Fact]
    public void Roles_ReadADottedPathInsideAJsonClaim_LikeKeycloakRealmRoles()
    {
        var provider = new OidcProviderOptions { Name = "kc", RoleClaim = "realm_access.roles" };
        var user = User(new Claim("realm_access", """{"roles":["offline_access","admin"]}"""));
        Assert.Equal(AuthRoles.Admin, OidcClaims.Role(provider, user));
        Assert.Null(OidcClaims.Role(provider, User(new Claim("realm_access", "not json"))));
    }

    [Fact]
    public void Roles_ReadAJsonArrayValue()
    {
        var provider = new OidcProviderOptions { Name = "p" };
        Assert.Equal(AuthRoles.Viewer, OidcClaims.Role(provider, User(new Claim("roles", """["viewer"]"""))));
    }

    [Fact]
    public void DisplayName_PrefersTheUserName()
    {
        Assert.Equal("alice", OidcClaims.DisplayName(User(new Claim("sub", "1"), new Claim("email", "a@example.com"), new Claim("preferred_username", "alice"))));
        Assert.Equal("a@example.com", OidcClaims.DisplayName(User(new Claim("sub", "1"), new Claim("email", "a@example.com"))));
    }
}
