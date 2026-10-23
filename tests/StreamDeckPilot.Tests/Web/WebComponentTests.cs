using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Web.Components.Layout;
using StreamDeckPilot.Web.Components.Pages;
using StreamDeckPilot.Web.Services;

namespace StreamDeckPilot.Tests.Web;

/// <summary>bUnit tests for the web shell (#96): device switcher, navigation and placeholder pages.</summary>
public sealed class WebComponentTests : BunitContext
{
    private readonly FakeDeviceDirectory _directory = new();

    public WebComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;   // MudBlazor calls JS for popovers, theme detection…
        Services.AddMudServices();
        Services.AddSingleton<IDeviceDirectory>(_directory);
        Services.AddScoped<DeviceSelection>();
    }

    private static DeviceSummary Deck(string serial, DeviceConnectionState state = DeviceConnectionState.Connected) =>
        new(serial, "Stream Deck MK.2", 3, 5, state);

    // ── Home ────────────────────────────────────────────────────────────────

    [Fact]
    public void Home_NoDecks_SaysSo()
    {
        var cut = Render<Home>();
        Assert.NotNull(cut.Find("[data-testid=no-devices]"));
    }

    [Fact]
    public void Home_ListsEveryDeck_WithItsConnectionState()
    {
        _directory.Devices = [Deck("EXAMPLE-A"), Deck("EXAMPLE-B", DeviceConnectionState.Disconnected)];

        var cards = Render<Home>().FindAll("[data-testid=device-card]");

        Assert.Equal(2, cards.Count);
        Assert.Contains("EXAMPLE-A", cards[0].TextContent);
        Assert.Contains("Connected", cards[0].TextContent);
        Assert.Contains("Not connected", cards[1].TextContent);
    }

    // ── Device switcher ─────────────────────────────────────────────────────

    [Fact]
    public void Switcher_OneDeck_SelectsIt()
    {
        _directory.Devices = [Deck("EXAMPLE-A")];

        var cut = Render<DeviceSwitcher>();

        Assert.Equal("EXAMPLE-A", Services.GetRequiredService<DeviceSelection>().Serial);
        Assert.Contains("EXAMPLE-A", cut.Markup);
    }

    [Fact]
    public void Switcher_SeveralDecks_WaitsForAChoice_ThenNavigates()
    {
        _directory.Devices = [Deck("EXAMPLE-A"), Deck("EXAMPLE-B")];
        var cut = Render(b =>
        {
            b.OpenComponent<MudPopoverProvider>(0);
            b.CloseComponent();
            b.OpenComponent<DeviceSwitcher>(1);
            b.CloseComponent();
        });
        var selection = Services.GetRequiredService<DeviceSelection>();
        Assert.Null(selection.Serial);
        Assert.Contains("Choose a deck", cut.Markup);

        cut.Find("[data-testid=device-switcher] button").Click();
        var options = cut.FindAll("[data-testid=device-option]");
        Assert.Equal(2, options.Count);
        options[1].Click();

        Assert.Equal("EXAMPLE-B", selection.Serial);
        Assert.EndsWith("/deck/EXAMPLE-B", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void Switcher_NoDecks_SaysSo()
    {
        var cut = Render<DeviceSwitcher>();
        Assert.Contains("No decks", cut.Markup);
    }

    // ── Deck page and navigation ────────────────────────────────────────────

    [Fact]
    public void DeckPage_SelectsItsDeck_AndTheNavShowsIt()
    {
        _directory.Devices = [Deck("EXAMPLE-A")];
        var nav = Render<NavMenu>();
        Assert.DoesNotContain("deck/", nav.Markup);

        var page = Render<Deck>(p => p.Add(d => d.Serial, "EXAMPLE-A"));

        Assert.NotNull(page.Find("[data-testid=deck-summary]"));
        Assert.Equal("EXAMPLE-A", Services.GetRequiredService<DeviceSelection>().Serial);
        nav.WaitForAssertion(() => Assert.Contains("deck/EXAMPLE-A", nav.Markup));
    }

    [Fact]
    public void DeckPage_UnknownDeck_SaysSo()
    {
        var page = Render<Deck>(p => p.Add(d => d.Serial, "NOT-CATALOGUED"));
        Assert.NotNull(page.Find("[data-testid=unknown-device]"));
    }

    private sealed class FakeDeviceDirectory : IDeviceDirectory
    {
        public IReadOnlyList<DeviceSummary> Devices { get; set; } = [];

        public Task<IReadOnlyList<DeviceSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Devices);
    }
}
