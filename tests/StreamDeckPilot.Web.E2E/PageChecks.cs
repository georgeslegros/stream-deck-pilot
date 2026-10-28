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
            // Chromium aborts in-flight loads with ERR_NETWORK_CHANGED when the host's interfaces
            // change, which happens on CI runners while Docker sets up the MQTT test containers
            // (#143). That's the runner, not the app: start the load again, once.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await page.GotoAsync(path);
                    await page.WaitForSelectorAsync("[data-interactive=true]", new() { State = WaitForSelectorState.Attached, Timeout = 15_000 });
                    break;
                }
                catch (Exception ex) when (attempt == 1 && ex is TimeoutException or PlaywrightException && NetworkChanged(ex))
                {
                    lock (console) console.Clear();
                }
            }

            bool NetworkChanged(Exception ex)
            {
                if (ex.Message.Contains("ERR_NETWORK_CHANGED")) return true;
                lock (console) return console.Exists(line => line.Contains("ERR_NETWORK_CHANGED"));
            }
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

    /// <summary>
    /// Waits until the page's URL ends with <paramref name="path"/> after an in-app navigation.
    /// Blazor navigates with history.pushState: no new document and no load event, and when the
    /// URL had already changed by the time WaitForURLAsync started, it never resolved (#140).
    /// Checking the URL itself has no such race.
    /// </summary>
    public static Task WaitForInAppNavigationAsync(this IPage page, string path) =>
        Assertions.Expect(page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(path) + "$"),
            new() { Timeout = 10_000 });

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
