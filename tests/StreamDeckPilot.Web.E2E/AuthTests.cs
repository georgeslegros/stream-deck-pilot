using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using StreamDeckPilot.Web.Auth;

namespace StreamDeckPilot.Web.E2E;

/// <summary>Sign-in in a real browser (#97): the pages fit every screen, and sign-in, the dashboard and sign-out work.</summary>
public sealed class AuthTests(WebAppFixture app)
{
    [Theory]
    [MemberData(nameof(Viewport.All), MemberType = typeof(Viewport))]
    public async Task SignInPage_FitsTheScreen_WithLargeEnoughTouchTargets(Viewport viewport)
    {
        var page = await app.NewPageAsync(viewport, signedIn: false);
        await page.GotoInteractiveAsync("account/signin");

        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("Sign in");
        await page.AssertNoHorizontalScrollAsync();
        await page.AssertTouchTargetsAsync();
    }

    [Theory]
    [MemberData(nameof(Viewport.All), MemberType = typeof(Viewport))]
    public async Task SignIn_ShowsTheDashboard_AndSignOutEndsTheSession(Viewport viewport)
    {
        var page = await app.NewPageAsync(viewport, signedIn: false);

        // Signed out, the dashboard sends the browser to sign-in and back afterwards.
        await page.GotoAsync($"board/{WebAppFixture.Serial}");
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/account/signin\\?returnUrl="));
        await page.FillAsync("[data-testid=signin-username]", WebAppFixture.AdminUser);
        await page.FillAsync("[data-testid=signin-password]", WebAppFixture.AdminPassword);
        await page.ClickAsync("[data-testid=signin-submit]");
        await page.WaitForSelectorAsync("[data-interactive=true]", new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });
        await page.WaitForInAppNavigationAsync($"/board/{WebAppFixture.Serial}");
        await Assertions.Expect(page.Locator($"[data-testid=device-card][data-serial='{WebAppFixture.Serial}']")).ToBeVisibleAsync();
        await page.AssertNoHorizontalScrollAsync();

        await page.ClickAsync("[data-testid=signout]");
        await page.WaitForInAppNavigationAsync("/account/signin");
        await page.GotoAsync("");
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/account/signin"));
    }

    [Fact]
    public async Task WrongPassword_SaysSo_AndStaysOnSignIn()
    {
        var page = await app.NewPageAsync(Viewport.Phone, signedIn: false);
        await page.GotoInteractiveAsync("account/signin");
        await page.FillAsync("[data-testid=signin-username]", WebAppFixture.AdminUser);
        await page.FillAsync("[data-testid=signin-password]", "not the password");
        await page.ClickAsync("[data-testid=signin-submit]");

        await Assertions.Expect(page.Locator("[data-testid=signin-error]")).ToContainTextAsync("Wrong user name or password");
        await page.AssertNoHorizontalScrollAsync();
    }

    [Theory]
    [MemberData(nameof(Viewport.All), MemberType = typeof(Viewport))]
    public async Task FirstRunSetup_FitsTheScreen_AndCreatesTheAdmin(Viewport viewport)
    {
        // A fresh install of its own: no account yet, so sign-in leads to setup.
        await using var fresh = new WebAppFixture.AppFactory();
        fresh.UseKestrel(0);
        fresh.StartServer();
        var baseUrl = fresh.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        if (app.Browser is null) Assert.Skip(app.Unavailable ?? "No browser");
        await using var context = await app.Browser.NewContextAsync(new()
        {
            BaseURL = baseUrl,
            ViewportSize = new() { Width = viewport.Width, Height = viewport.Height },
            IsMobile = viewport.IsMobile,
            HasTouch = viewport.IsMobile,
        });
        var page = await context.NewPageAsync();
        await page.GotoInteractiveAsync("account/signin");
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/account/setup$"));
        await page.AssertNoHorizontalScrollAsync();
        await page.AssertTouchTargetsAsync();

        await page.FillAsync("[data-testid=setup-key]", WebAppFixture.ApiKey);
        await page.FillAsync("[data-testid=setup-username]", "owner");
        await page.FillAsync("[data-testid=setup-password]", "a long enough password");
        await page.FillAsync("[data-testid=setup-confirm]", "a long enough password");
        await page.ClickAsync("[data-testid=setup-submit]");

        await page.WaitForSelectorAsync("[data-testid=nav-menu]", new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });
        Assert.True(await fresh.Services.GetRequiredService<LocalUserStore>().AnyAsync());
    }
}
