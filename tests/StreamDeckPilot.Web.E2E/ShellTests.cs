using Microsoft.Playwright;

namespace StreamDeckPilot.Web.E2E;

/// <summary>The app shell (#96) at phone, tablet and desktop sizes.</summary>
public sealed class ShellTests(WebAppFixture app)
{
    private static readonly string[] Pages = ["/", $"/deck/{WebAppFixture.Serial}"];

    [Theory]
    [MemberData(nameof(Viewport.All), MemberType = typeof(Viewport))]
    public async Task EveryPage_FitsTheScreen_WithLargeEnoughTouchTargets(Viewport viewport)
    {
        var page = await app.NewPageAsync(viewport);
        foreach (var path in Pages)
        {
            await page.GotoInteractiveAsync(path);
            await page.AssertNoHorizontalScrollAsync();
            await page.AssertTouchTargetsAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Viewport.All), MemberType = typeof(Viewport))]
    public async Task Navigation_IsReachable_AndNavigates(Viewport viewport)
    {
        var page = await app.NewPageAsync(viewport);
        await page.GotoInteractiveAsync("/");

        var nav = await page.OpenNavAsync(viewport);
        await page.AssertTouchTargetsAsync();   // with the drawer open too
        await nav.GetByText($"Deck {WebAppFixture.Serial}").ClickAsync();

        await page.WaitForURLAsync($"**/deck/{WebAppFixture.Serial}");
        await Assertions.Expect(page.Locator("h1")).ToContainTextAsync(WebAppFixture.Serial);
    }

    [Theory]
    [MemberData(nameof(Viewport.All), MemberType = typeof(Viewport))]
    public async Task DeviceSwitcher_ListsTheDeck_AndOpensIt(Viewport viewport)
    {
        var page = await app.NewPageAsync(viewport);
        await page.GotoInteractiveAsync("/");

        await page.Locator("[data-testid=device-switcher] button").ClickAsync();
        var option = page.Locator("[data-testid=device-option]");
        await Assertions.Expect(option).ToHaveCountAsync(1);
        await page.AssertTouchTargetsAsync();   // with the menu open too
        await page.AssertNoHorizontalScrollAsync();
        await option.ClickAsync();

        await page.WaitForURLAsync($"**/deck/{WebAppFixture.Serial}");
        await Assertions.Expect(page.Locator("[data-testid=deck-summary]")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Theme_FollowsTheOperatingSystem()
    {
        var light = await app.NewPageAsync(Viewport.Desktop, ColorScheme.Light);
        var dark = await app.NewPageAsync(Viewport.Desktop, ColorScheme.Dark);
        await light.GotoInteractiveAsync("/");
        await dark.GotoInteractiveAsync("/");

        Assert.True(await BackgroundLuminanceAsync(light) > 0.5, "light scheme should give a light page");
        Assert.True(await WaitForDarkAsync(dark), "dark scheme should give a dark page");
    }

    private static async Task<bool> WaitForDarkAsync(IPage page)
    {
        // The theme switches once the circuit has asked the browser for its preference.
        for (var i = 0; i < 50; i++)
        {
            if (await BackgroundLuminanceAsync(page) < 0.5) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static Task<double> BackgroundLuminanceAsync(IPage page) => page.EvaluateAsync<double>(
        """
        () => {
          const [r, g, b] = getComputedStyle(document.body).backgroundColor.match(/\d+/g).map(Number);
          return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        }
        """);
}
