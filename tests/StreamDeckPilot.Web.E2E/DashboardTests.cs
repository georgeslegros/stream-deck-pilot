using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Infrastructure.Rendering;

namespace StreamDeckPilot.Web.E2E;

/// <summary>
/// The read-only dashboard (#99) in a real browser: the test deck, its board as images, its other
/// pages on demand, its bound topics, and pushed updates arriving without a reload or any polling.
/// </summary>
public sealed class DashboardTests(WebAppFixture app)
{
    private const string Serial = WebAppFixture.Serial;

    [Theory]
    [MemberData(nameof(Viewport.All), MemberType = typeof(Viewport))]
    public async Task Dashboard_ShowsTheDeck_ItsBoard_ItsPages_AndItsTopics(Viewport viewport)
    {
        var page = await app.NewPageAsync(viewport);
        await page.GotoInteractiveAsync("/");

        await Assertions.Expect(page.Locator($"[data-testid=device-card][data-serial='{Serial}']")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid=broker-card]")).ToBeVisibleAsync();
        var keys = page.Locator($"[data-testid=live-board][data-serial='{Serial}'] img[data-testid=board-key]");
        await Assertions.Expect(keys).ToHaveCountAsync(15);
        Assert.StartsWith("data:image/png;base64,", await keys.First.GetAttributeAsync("src"));

        // co2, temperature, and the setpoint's current and target.
        await Assertions.Expect(page.Locator("[data-testid=bound-topic]")).ToHaveCountAsync(4);

        // Other pages are collapsed and rendered when opened.
        var panel = page.Locator("[data-testid=page-panel][data-page=climate]");
        await Assertions.Expect(page.Locator("[data-testid=page-image]")).ToHaveCountAsync(0);
        await panel.Locator(".mud-expand-panel-header").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid=page-image][data-page=climate]")).ToBeVisibleAsync();

        await page.AssertNoHorizontalScrollAsync();
        await page.AssertTouchTargetsAsync();
    }

    [Fact]
    public async Task PushedValue_AndKeyChange_ShowWithoutAReload()
    {
        var bus = app.Services.GetRequiredService<IEventBus>();
        var desired = app.Services.GetRequiredService<DesiredStateStore>();
        var page = await app.NewPageAsync(Viewport.Phone);
        await page.GotoInteractiveAsync($"/board/{Serial}");
        var navigations = 0;
        page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) Interlocked.Increment(ref navigations); };

        var co2 = page.Locator($"[data-testid=bound-topic][data-topic='{WebAppFixture.Co2Topic}']");
        var key0 = page.Locator("[data-testid=live-board] [data-testid=board-key][data-key='0']");
        await Assertions.Expect(key0).ToHaveAttributeAsync("data-etag", new System.Text.RegularExpressions.Regex(".+"));
        var before = await key0.GetAttributeAsync("data-etag");
        var key1Before = await page.Locator("[data-testid=live-board] [data-testid=board-key][data-key='1']").GetAttributeAsync("data-etag");

        // A value on a bound topic: the topic list shows it.
        var value = Random.Shared.Next(400, 2000).ToString();
        bus.Publish(new ValueReceived(WebAppFixture.Co2Topic, value));
        await Assertions.Expect(co2.Locator("[data-testid=topic-value]")).ToContainTextAsync(value, new() { Timeout = 2_000 });

        // The key's desired state changes (the deck isn't connected): the board swaps that key's image.
        desired.Set(Serial, "main", 0, new ButtonRenderState("co2", "#CC3300", null, IconPlacement.Corner, value, "CO2"));
        await Assertions.Expect(key0).Not.ToHaveAttributeAsync("data-etag", before!, new() { Timeout = 2_000 });
        Assert.Equal(key1Before, await page.Locator("[data-testid=live-board] [data-testid=board-key][data-key='1']").GetAttributeAsync("data-etag"));

        Assert.Equal(0, navigations);
    }
}
