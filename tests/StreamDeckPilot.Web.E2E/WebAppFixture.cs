using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using StreamDeckPilot.Infrastructure.Persistence;

[assembly: AssemblyFixture(typeof(StreamDeckPilot.Web.E2E.WebAppFixture))]

namespace StreamDeckPilot.Web.E2E;

/// <summary>
/// One app (UI on, real Kestrel port, one generic deck in the catalogue) and one Chromium for the
/// whole test assembly. If the browser isn't installed, tests skip — unless E2E_REQUIRED=1 (CI).
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    public const string ApiKey = "e2e-api-key-0123456789";
    public const string Serial = "EXAMPLE-0001";

    private readonly AppFactory _factory = new();
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = "";

    /// <summary>The running app's services, e.g. to push a live event (#108).</summary>
    public IServiceProvider Services => _factory.Services;
    public IBrowser? Browser { get; private set; }
    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        BaseUrl = _factory.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        await _factory.Services.GetRequiredService<CatalogueStore>().AppendDeviceAsync(
            new(Serial, "Stream Deck MK.2", 3, 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

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

    /// <summary>A fresh page at the given viewport, or a skip when no browser is available.</summary>
    public async Task<IPage> NewPageAsync(Viewport viewport, ColorScheme scheme = ColorScheme.Light)
    {
        if (Browser is null) Assert.Skip(Unavailable ?? "No browser");
        var context = await Browser.NewContextAsync(new()
        {
            BaseURL = BaseUrl,
            ViewportSize = new() { Width = viewport.Width, Height = viewport.Height },
            IsMobile = viewport.IsMobile,
            HasTouch = viewport.IsMobile,
            ColorScheme = scheme,
        });
        return await context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        await _factory.DisposeAsync();
    }

    private sealed class AppFactory : WebApplicationFactory<Program>
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
