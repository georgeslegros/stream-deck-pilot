using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace StreamDeckPilot.Web.E2E;

/// <summary>The admin-only API keys page (#98): create a key, see it once, revoke it.</summary>
public sealed class ApiKeysTests(WebAppFixture app)
{
    private async Task<HttpStatusCode> CallApiAsync(string key)
    {
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Add("X-Api-Key", key);
        return (await http.GetAsync("/devices")).StatusCode;
    }

    [Theory]
    [MemberData(nameof(Viewport.All), MemberType = typeof(Viewport))]
    public async Task Admin_CreatesAKey_SeesItOnce_AndRevokesIt(Viewport viewport)
    {
        var name = $"e2e {viewport} {Guid.NewGuid():N}"[..30];
        var page = await app.NewPageAsync(viewport);
        await page.GotoInteractiveAsync("/admin/api-keys");

        await page.Locator("input[data-testid=key-name], [data-testid=key-name] input").First.FillAsync(name);
        await page.Locator("[data-testid=create-key]").ClickAsync();

        var shown = page.Locator("[data-testid=new-key]");
        await Assertions.Expect(shown).ToHaveValueAsync(new Regex("^sdp_"));
        var key = await shown.InputValueAsync();
        await Assertions.Expect(page.Locator("[data-testid=new-key-panel]")).ToContainTextAsync("won't see it again");
        await page.AssertNoHorizontalScrollAsync();
        await page.AssertTouchTargetsAsync();
        Assert.Equal(HttpStatusCode.OK, await CallApiAsync(key));

        // Once: gone after "Done", and not on the page when it's opened again.
        await page.Locator("[data-testid=dismiss-key]").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid=new-key-panel]")).ToHaveCountAsync(0);
        await page.GotoInteractiveAsync("/admin/api-keys");
        Assert.DoesNotContain(key, await page.ContentAsync());

        var row = page.Locator($"[data-testid=key-row][data-key-name='{name}']");
        await Assertions.Expect(row).ToHaveAttributeAsync("data-status", "Active");
        await row.Locator("[data-testid=revoke]").ClickAsync();
        await row.Locator("[data-testid=confirm-revoke]").ClickAsync();
        await Assertions.Expect(row).ToHaveAttributeAsync("data-status", "Revoked");
        Assert.Equal(HttpStatusCode.Unauthorized, await CallApiAsync(key));
    }

    [Fact]
    public async Task Viewer_DoesntSeeTheLink_AndCantOpenThePage()
    {
        var page = await app.NewPageAsync(Viewport.Desktop, asViewer: true);
        await page.GotoInteractiveAsync("/");
        await Assertions.Expect(page.Locator("[data-testid=nav-menu]")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid=nav-api-keys]")).ToHaveCountAsync(0);

        await page.GotoAsync("/admin/api-keys");
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/account/denied"));
        await Assertions.Expect(page.Locator("[data-testid=create-key]")).ToHaveCountAsync(0);
    }
}
