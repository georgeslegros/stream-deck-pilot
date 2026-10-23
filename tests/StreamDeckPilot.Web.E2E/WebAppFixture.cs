using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Web.Auth;

[assembly: AssemblyFixture(typeof(StreamDeckPilot.Web.E2E.WebAppFixture))]

namespace StreamDeckPilot.Web.E2E;

/// <summary>
/// One app (UI on, real Kestrel port, one generic deck in the catalogue, one local admin) and one
/// Chromium for the whole test assembly. Pages start signed in (#97) unless a test asks otherwise.
/// If the browser isn't installed, tests skip — unless E2E_REQUIRED=1 (CI).
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    public const string ApiKey = "e2e-api-key-0123456789";
    public const string Serial = "EXAMPLE-0001";
    public const string AdminUser = "admin";
    public const string AdminPassword = "e2e admin password";

    private readonly AppFactory _factory = new();
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = "";

    /// <summary>A generic two-page layout for the test deck: bound values, a setpoint and navigation.</summary>
    public const string Co2Topic = "example/sensors/co2";

    public static readonly DeviceConfig SampleConfig = new(2, Serial,
    [
        new ButtonGridPage("main",
        [
            new ButtonDefinition("co2", 0, "main", new DisplaySpec(Center: new TextZone(null, "{value}"), Bottom: new TextZone("CO2", null)),
                new InboundBinding(Co2Topic, null, null, false, TimeSpan.FromMinutes(5)), [], NoGestures),
            new ButtonDefinition("temperature", 1, "main", new DisplaySpec(Center: new TextZone(null, "{value}")),
                new InboundBinding("example/sensors/temperature", null, null, false, TimeSpan.FromMinutes(5)), [], NoGestures),
            new ButtonDefinition("to-climate", 4, "main", new DisplaySpec(Center: new TextZone("Climate", null)), null, [],
                new Dictionary<string, IReadOnlyList<ButtonAction>> { ["Tap"] = [new NavigateAction("climate")] }),
        ]),
        new ButtonGridPage("climate", [],
        [
            new SetpointControl("room", 1, SetpointOrientation.Vertical,
                new SetpointBinding("example/climate/room/current"), new SetpointBinding("example/climate/room/target"),
                new SetpointCommit("example/climate/room/set"), 0.5, 5, 30, Label: "Room", Unit: "°"),
        ]),
    ]);

    private static readonly Dictionary<string, IReadOnlyList<ButtonAction>> NoGestures = new();

    /// <summary>The running app's services, e.g. to push a live event (#108).</summary>
    public IServiceProvider Services => _factory.Services;
    public IBrowser? Browser { get; private set; }
    public string? Unavailable { get; private set; }

    // Cookies of a signed-in session, shared by every page that starts signed in.
    private string? _signedIn;

    public async ValueTask InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        BaseUrl = _factory.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        await _factory.Services.GetRequiredService<CatalogueStore>().AppendDeviceAsync(
            new(Serial, "Stream Deck MK.2", 3, 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await _factory.Services.GetRequiredService<ConfigStore>().SaveAsync(SampleConfig);
        await _factory.Services.GetRequiredService<LocalUserStore>().CreateAsync(AdminUser, AdminPassword, AuthRoles.Admin);

        try
        {
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        }
        catch (PlaywrightException ex) when (Environment.GetEnvironmentVariable("E2E_REQUIRED") != "1")
        {
            Unavailable = $"Playwright browser not installed ({ex.Message.Split('\n')[0]}). " +
                          "Run tests/StreamDeckPilot.Web.E2E/bin/<config>/net10.0/playwright.ps1 install chromium.";
        }
    }

    /// <summary>
    /// A fresh page at the given viewport, signed in as the admin unless <paramref name="signedIn"/>
    /// is false, or a skip when no browser is available.
    /// </summary>
    public async Task<IPage> NewPageAsync(Viewport viewport, ColorScheme scheme = ColorScheme.Light, bool signedIn = true)
    {
        if (Browser is null) Assert.Skip(Unavailable ?? "No browser");
        if (signedIn) _signedIn ??= await SignInOnceAsync();
        var context = await Browser.NewContextAsync(new()
        {
            BaseURL = BaseUrl,
            ViewportSize = new() { Width = viewport.Width, Height = viewport.Height },
            IsMobile = viewport.IsMobile,
            HasTouch = viewport.IsMobile,
            ColorScheme = scheme,
            StorageState = signedIn ? _signedIn : null,
        });
        return await context.NewPageAsync();
    }

    private async Task<string> SignInOnceAsync()
    {
        await using var context = await Browser!.NewContextAsync(new() { BaseURL = BaseUrl });
        var page = await context.NewPageAsync();
        await page.SignInAsync(AdminUser, AdminPassword);
        return await context.StorageStateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>The app with the UI on and its own empty data directory (deleted on dispose).</summary>
    internal sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly string _storage = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Serve static web assets (MudBlazor, blazor.web.js, app.css) from the build output,
            // as Development does; outside Development they're only found in a published wwwroot.
            builder.UseStaticWebAssets();
            builder.UseSetting("Web:Enabled", "true");
            builder.UseSetting("ApiKey", ApiKey);
            builder.UseSetting("Storage:BaseDirectory", _storage);
            // Every test signs in from 127.0.0.1; the per-address limit is covered by AuthTests in
            // StreamDeckPilot.Tests.
            builder.UseSetting("Auth:AttemptLimit", "1000");
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
        }
    }
}

/// <summary>The three sizes every UI page is checked at (#96 responsive acceptance).</summary>
public sealed record Viewport(string Name, int Width, int Height, bool IsMobile)
{
    public static readonly Viewport Phone = new("phone", 360, 740, true);
    public static readonly Viewport Tablet = new("tablet", 768, 1024, true);
    public static readonly Viewport Desktop = new("desktop", 1440, 900, false);

    public static TheoryData<Viewport> All => new(Phone, Tablet, Desktop);

    /// <summary>Below MudBlazor's md breakpoint (960 px) the navigation is a drawer behind the menu button.</summary>
    public bool HasDrawer => Width < 960;

    public override string ToString() => Name;
}
