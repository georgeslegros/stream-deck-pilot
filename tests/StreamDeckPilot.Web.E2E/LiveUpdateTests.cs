using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using StreamDeckPilot.Infrastructure.Events;

namespace StreamDeckPilot.Web.E2E;

/// <summary>
/// Live updates reach the browser over the circuit (#108): an event on the bus changes the page
/// without a reload or any polling. (The test deck has no hardware behind it, so the test plays
/// the device runtime and publishes the connection events itself.)
/// </summary>
public sealed class LiveUpdateTests(WebAppFixture app)
{
    [Theory]
    [MemberData(nameof(Viewport.All), MemberType = typeof(Viewport))]
    public async Task ConnectionState_IsPushed_WithoutAReload(Viewport viewport)
    {
        var bus = app.Services.GetRequiredService<IEventBus>();
        var page = await app.NewPageAsync(viewport);
        await page.GotoInteractiveAsync($"/board/{WebAppFixture.Serial}");
        var navigations = 0;
        page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) Interlocked.Increment(ref navigations); };

        var icon = page.Locator("[data-testid=connection-state]");
        var summary = page.Locator("[data-testid=deck-state]");
        await Assertions.Expect(icon).ToHaveAttributeAsync("data-state", "disconnected");
        await Assertions.Expect(summary).ToHaveTextAsync("Not connected");

        try
        {
            bus.Publish(new DeviceConnected { Serial = WebAppFixture.Serial });
            await Assertions.Expect(icon).ToHaveAttributeAsync("data-state", "connected", new() { Timeout = 2_000 });
            await Assertions.Expect(summary).ToHaveTextAsync("Connected", new() { Timeout = 2_000 });
            await page.AssertNoHorizontalScrollAsync();
        }
        finally
        {
            bus.Publish(new DeviceDisconnected { Serial = WebAppFixture.Serial });
        }
        await Assertions.Expect(icon).ToHaveAttributeAsync("data-state", "disconnected", new() { Timeout = 2_000 });

        Assert.Equal(0, navigations);
    }

    [Fact]
    public async Task ReconnectBanner_IsHidden_WhileConnected()
    {
        var page = await app.NewPageAsync(Viewport.Phone);
        await page.GotoInteractiveAsync("/");
        await Assertions.Expect(page.Locator("[data-testid=reconnect-banner]")).ToBeHiddenAsync();
    }
}
