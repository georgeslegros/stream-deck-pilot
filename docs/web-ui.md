# Web UI

The browser UI for Stream Deck Pilot (epic #95). This page covers turning it on, running it locally,
how it is put together, and its tests.

## Turning it on

The UI is **off by default**. Set `Web__Enabled=true` (environment variable) or `"Web": { "Enabled": true }`
(appsettings) and it is served under `/` by the same process and port as the REST API.

> **Private networks only, for now.** Sign-in arrives with #97 (v0.10.0). Until then anyone who can
> reach the UI can see your decks, so only enable it where the service is reachable from your LAN or
> VPN, never from the internet. Every page is read-only in the meantime.

What doesn't change:

- The REST API, `/health` and `/openapi.json` behave exactly as before. The API still needs the
  `X-Api-Key` header.
- Unknown paths still answer `401` without the key, as they always did.

UI pages, the Blazor connection (`/_blazor`) and static files (`/_framework/…`, `/_content/…`) don't
need the key: a browser doesn't send one. UI routes never overlap API routes (`/devices…`, `/config…`).

## Running it locally

```bash
Web__Enabled=true ApiKey=dev-key dotnet run --project src/StreamDeckPilot.Api
# open http://localhost:5177
```

`dotnet run` uses the `Development` environment from `launchSettings.json`, which serves the static
files (MudBlazor, `blazor.web.js`) from the build output. A published app (the Docker image) serves
them from its `wwwroot`. Without a deck attached, the catalogue is whatever is in
`Storage__BaseDirectory`; an empty one shows "No decks discovered yet".

## How it is built

- **Blazor, Interactive Server only.** Components run on the server and update the browser over one
  WebSocket per tab (the Blazor circuit). No WebAssembly, no separate front-end build.
- **`src/StreamDeckPilot.Web`** is a Razor class library holding the components, their CSS and the
  wiring (`WebUiExtensions.AddWebUi` / `MapWebUi`). The API host only calls those two methods.
- **MudBlazor**, pinned to one exact version in `StreamDeckPilot.Web.csproj`. Upgrade it deliberately.
- **Layout** (`Components/Layout/MainLayout.razor`): app bar with the menu button and the device
  switcher, a side bar from 960 px up and a drawer below that, light/dark following the OS.
- **Device switcher**: lists the catalogue (decks seen at least once) with their connection state.
  The chosen deck is per browser tab (`DeviceSelection`, scoped to the circuit). With one deck it is
  chosen automatically.

### Adding a page

1. Add `Components/Pages/MyPage.razor` with `@page "/my-page"`. Don't use a route the API uses
   (`/devices…`, `/config…`, `/health`, `/openapi.json`), and don't start one with `/deck`: that is
   the example path base for running behind a proxy. Current pages: `/` (overview) and
   `/board/{serial}` (one deck).
2. Add a `MudNavLink` in `Components/Layout/NavMenu.razor`.
3. Use the injected services (`IDeviceDirectory`, `DeviceSelection`); register new ones in
   `WebUiExtensions.AddWebUi`.
4. Give the page an `h1` (`<MudText Typo="Typo.h4" HtmlTag="h1">`), and add the path to
   `ShellTests.Pages` in the E2E project so it is checked at every viewport.

### Responsive rules

Every page must work from 360 px wide with no horizontal scrolling, and every link, button or menu
item must be at least 44 × 44 px. `wwwroot/app.css` holds the shared rules (`sdp-break` wraps long
serials and topics). The E2E tests check both at three sizes.

## Tests

| Project | What | Needs |
|---|---|---|
| `tests/StreamDeckPilot.Tests/Web` | bUnit component tests (`WebComponentTests`) and hosting tests (`WebUiHostingTests`: off by default, API unchanged, UI exempt from the key) | nothing |
| `tests/StreamDeckPilot.Web.E2E` | Playwright (Chromium) against the real app on a Kestrel port, at phone 360×740, tablet 768×1024 and desktop 1440×900 | the Playwright browser |

Install the browser once after a build:

```bash
pwsh tests/StreamDeckPilot.Web.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
```

Without it, `dotnet test` skips the E2E tests with a message. CI installs it and sets
`E2E_REQUIRED=1`, so a missing browser fails there instead of skipping.

Helpers for new tests:

- `WebAppFixture`: one app (UI on, one generic deck `EXAMPLE-0001` in the catalogue) and one
  browser for the whole run. `NewPageAsync(viewport, colourScheme)` gives a fresh page.
- `PageChecks.GotoInteractiveAsync(path)`: loads a page and waits for the circuit (the layout sets
  `data-interactive="true"` once it is live). On timeout it reports the browser console.
- `AssertNoHorizontalScrollAsync()`, `AssertTouchTargetsAsync()`, `OpenNavAsync(viewport)`.
- `Viewport.All` for `[MemberData]`, and `Viewport.HasDrawer` for the layouts with a drawer.
