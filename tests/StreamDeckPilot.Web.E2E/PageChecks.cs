using Microsoft.Playwright;

namespace StreamDeckPilot.Web.E2E;

/// <summary>Reusable checks for every UI page: load, responsive layout, touch targets.</summary>
public static class PageChecks
{
    /// <summary>Opens a page and waits until the Blazor circuit is interactive.</summary>
    public static async Task GotoInteractiveAsync(this IPage page, string path)
    {
        var console = new List<string>();
        void OnConsole(object? _, IConsoleMessage m) { lock (console) console.Add($"{m.Type}: {m.Text}"); }
        void OnFailed(object? _, IRequest r) { lock (console) console.Add($"request failed: {r.Url} {r.Failure}"); }
        void OnResponse(object? _, IResponse r) { if (r.Status >= 400) lock (console) console.Add($"HTTP {r.Status}: {r.Url}"); }
        void OnError(object? _, string e) { lock (console) console.Add($"page error: {e}"); }
        page.Console += OnConsole;
        page.PageError += OnError;
        page.RequestFailed += OnFailed;
        page.Response += OnResponse;
        try
        {
            await page.GotoAsync(path);
            await page.WaitForSelectorAsync("[data-interactive=true]", new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });
        }
        catch (TimeoutException ex)
        {
            // The browser console usually says why the circuit didn't start.
            var html = await page.ContentAsync();
            lock (console)
                throw new TimeoutException(
                    $"{page.Url} never became interactive. Browser console:\n{string.Join("\n", console)}\n" +
                    $"Page starts: {html[..Math.Min(html.Length, 600)]}", ex);
        }
        finally
        {
            page.Console -= OnConsole;
            page.PageError -= OnError;
            page.RequestFailed -= OnFailed;
            page.Response -= OnResponse;
        }
    }

    public static async Task AssertNoHorizontalScrollAsync(this IPage page)
    {
        var widths = await page.EvaluateAsync<int[]>(
            "() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]");
        var (scroll, client) = (widths[0], widths[1]);
        Assert.True(scroll <= client, $"Horizontal scroll on {page.Url}: scrollWidth {scroll} > clientWidth {client}");
    }

    /// <summary>Every visible link, button and menu item is at least 44 × 44 CSS pixels.</summary>
    public static async Task AssertTouchTargetsAsync(this IPage page, int minimum = 44)
    {
        var small = await page.EvaluateAsync<string[]>(
            """
            (min) => [...document.querySelectorAll('a[href], button, [role=button], [role=menuitem], input, select, textarea')]
              .filter(e => {
                const r = e.getBoundingClientRect();
                const s = getComputedStyle(e);
                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
              })
              .filter(e => { const r = e.getBoundingClientRect(); return r.width < min || r.height < min; })
              .map(e => {
                const r = e.getBoundingClientRect();
                const label = e.getAttribute('aria-label') || e.getAttribute('data-testid') || e.textContent.trim().slice(0, 30);
                return `${e.tagName.toLowerCase()} "${label}" ${Math.round(r.width)}x${Math.round(r.height)}`;
              })
            """, minimum);
        Assert.True(small.Length == 0, $"Touch targets under {minimum}px on {page.Url}: {string.Join("; ", small)}");
    }

    /// <summary>The navigation is reachable: opens the drawer on small screens first.</summary>
    public static async Task<ILocator> OpenNavAsync(this IPage page, Viewport viewport)
    {
        var nav = page.Locator("[data-testid=nav-menu]");
        if (viewport.HasDrawer)
        {
            await page.Locator("[data-testid=nav-toggle]").ClickAsync();
        }
        await nav.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        return nav;
    }
}
