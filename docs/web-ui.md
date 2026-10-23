# Web UI

The browser UI for Stream Deck Pilot (epic #95). This page covers turning it on, running it locally,
how it is put together, and its tests.

## Turning it on

The UI is **off by default**. Set `Web__Enabled=true` (environment variable) or `"Web": { "Enabled": true }`
(appsettings) and it is served under `/` by the same process and port as the REST API.

**Every page needs a signed-in user** (#97): an OpenID Connect provider or a local account. On a
fresh install the first visit leads to setup, which creates the admin with a one-time token from
the service log. See [Signing in](auth.md). The pages are read-only for both roles so far.

What doesn't change:

- The REST API, `/health` and `/openapi.json` behave exactly as before. The API still needs the
  `X-Api-Key` header.
- Unknown paths still answer `401` without the key, as they always did.

UI pages and the Blazor connection (`/_blazor`) need a session instead of the key: a browser doesn't
send one. Signed out, a page redirects to `account/signin`; the connection answers `401`. Static
files (`/_framework/…`, `/_content/…`) are public, as the sign-in page uses them. UI routes never
overlap API routes (`/devices…`, `/config…`).

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
  chosen automatically. An icon next to it shows whether the chosen deck is connected.
- **Live updates** (#108): each tab has a `LiveState` (scoped to the circuit) fed by the event bus.
  Nothing polls; see [live-events.md](live-events.md#in-the-web-ui). A banner shows while the
  circuit's connection is down (`#components-reconnect-modal` in `App.razor`).

### The dashboard (#99)

The overview (`/`) and each deck's page (`/board/{serial}`) show the same read-only dashboard
(`Components/Dashboard`), for every deck or for one:

- **Broker**: connected or offline, whether the decks show the offline look yet, and the last
  message received.
- **Deck card**: connected or not, model, brightness and the page on screen, with a link to the
  deck's own page.
- **The page on screen**, key by key, as the deck shows it. When a key is repainted
  (`KeyRendered`), only that key's image is replaced, and only if its content hash changed. A deck
  that isn't connected shows its keys as it will show them when it connects (desired state).
- **Other pages**, collapsed. A page is rendered when you open it, and re-rendered while it is open
  when the deck's state changes, so a phone isn't sent every page.
- **Bound topics**: each topic a button or setpoint control listens to, with its latest payload
  and when it arrived. A topic is highlighted as **stale** when the deck shows its key dimmed for
  staleness; the dashboard reads that verdict from the renderer's state rather than working it out
  again, so the list and the keys always agree. A topic with no value since the service started is
  shown as waiting, not stale.

**Images** are rendered in-process (`DevicePreviewService`, the same code as the preview endpoint)
and sent over the circuit as `data:` URIs. The browser never fetches an image URL, so no image
endpoint is opened without the API key; the images are covered by the circuit's sign-in like
everything else on the page.

**Times and ages**: times are shown in the browser's time zone (asked once per tab). Ages ("2 min
ago") are worked out when the page renders, which it does on every pushed update; nothing refreshes
them on a timer (#108). The "As of" line under the title says when that was.

All of it is pushed: live events (#108) and desired-state changes (a key going stale writes no
event). Bursts are folded into at most one refresh in flight (`CoalescingRunner`).

### Adding a page

1. Add `Components/Pages/MyPage.razor` with `@page "/my-page"`. Don't use a route the API uses
   (`/devices…`, `/config…`, `/health`, `/openapi.json`), and don't start one with `/deck`: that is
   the example path base for running behind a proxy. Current pages: `/` (overview) and
   `/board/{serial}` (one deck).
2. Add a `MudNavLink` in `Components/Layout/NavMenu.razor`.
3. Use the injected services (`LiveState` for live data, `IDashboardSource` for the dashboard's
   data and images, `BrowserClock` for local times, `IDeviceDirectory`, `DeviceSelection`);
   register new ones in `WebUiExtensions.AddWebUi`. Subscribe to `Changed` events and re-render
   with `InvokeAsync(StateHasChanged)`; never refresh on a timer.
4. Give the page an `h1` (`<MudText Typo="Typo.h4" HtmlTag="h1">`), and add the path to
   `ShellTests.Pages` in the E2E project so it is checked at every viewport.
5. Pages need a viewer by default (the `Viewer` policy on every UI endpoint). A page that changes
   anything adds `@attribute [Authorize(Policy = AuthRoles.AdminPolicy)]`. Pages under
   `Components/Account` are the exception: `[AllowAnonymous]` and server-rendered
   (`[ExcludeFromInteractiveRouting]`), because they set the session cookie.

### Responsive rules

Every page must work from 360 px wide with no horizontal scrolling, and every link, button or menu
item must be at least 44 × 44 px. `wwwroot/app.css` holds the shared rules (`sdp-break` wraps long
serials and topics). The E2E tests check both at three sizes.

## Tests

| Project | What | Needs |
|---|---|---|
| `tests/StreamDeckPilot.Tests/Web` | bUnit component tests (`WebComponentTests`, `DashboardComponentTests` with `FakeDashboardSource`), hosting tests (`WebUiHostingTests`: off by default, API unchanged, UI exempt from the key) and sign-in (`AuthTests`, `OidcTests` against `FakeOidcProvider`, with `WebAuthFactory` and its `FormClient` for server-rendered forms) | nothing |
| `tests/StreamDeckPilot.Web.E2E` | Playwright (Chromium) against the real app on a Kestrel port, at phone 360×740, tablet 768×1024 and desktop 1440×900 | the Playwright browser |

Install the browser once after a build:

```bash
pwsh tests/StreamDeckPilot.Web.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
```

Without it, `dotnet test` skips the E2E tests with a message. CI installs it and sets
`E2E_REQUIRED=1`, so a missing browser fails there instead of skipping.

Helpers for new tests:

- `WebAppFixture`: one app (UI on, one generic deck `EXAMPLE-0001` in the catalogue with a
  two-page sample config, `SampleConfig`) and one browser for the whole run. `Services` reaches
  the app's services, e.g. to publish a live event or write desired state. It has one local admin
  (`AdminUser`/`AdminPassword`). `NewPageAsync(viewport, colourScheme)` gives a fresh page, already
  signed in; pass `signedIn: false` for a signed-out one. `page.SignInAsync(user, password)` signs in
  through the form.
- `PageChecks.GotoInteractiveAsync(path)`: loads a page and waits for the circuit (the layout sets
  `data-interactive="true"` once it is live). On timeout it reports the browser console.
- `AssertNoHorizontalScrollAsync()`, `AssertTouchTargetsAsync()`, `OpenNavAsync(viewport)`.
- `Viewport.All` for `[MemberData]`, and `Viewport.HasDrawer` for the layouts with a drawer.
