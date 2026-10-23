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
        Assert.EndsWith("/board/EXAMPLE-B", Services.GetRequiredService<NavigationManager>().Uri);
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
        Assert.DoesNotContain("board/", nav.Markup);

        var page = Render<Board>(p => p.Add(d => d.Serial, "EXAMPLE-A"));

        Assert.NotNull(page.Find("[data-testid=deck-summary]"));
        Assert.Equal("EXAMPLE-A", Services.GetRequiredService<DeviceSelection>().Serial);
        nav.WaitForAssertion(() => Assert.Contains("board/EXAMPLE-A", nav.Markup));
    }

    [Fact]
    public void DeckPage_UnknownDeck_SaysSo()
    {
        var page = Render<Board>(p => p.Add(d => d.Serial, "NOT-CATALOGUED"));
        Assert.NotNull(page.Find("[data-testid=unknown-device]"));
    }

    // ── Live updates (#108) ─────────────────────────────────────────────────

    [Fact]
    public void Home_UpdatesWhenAConnectionStateIsPushed()
    {
        _directory.Devices = [Deck("EXAMPLE-A", DeviceConnectionState.Disconnected)];
        var cut = Render<Home>();
        Assert.Contains("Not connected", cut.Find("[data-testid=device-card]").TextContent);

        _directory.Push([Deck("EXAMPLE-A")]);

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("Not connected", cut.Find("[data-testid=device-card]").TextContent));
    }

    [Fact]
    public void Switcher_ShowsTheChosenDecksConnection_AndFollowsIt()
    {
        _directory.Devices = [Deck("EXAMPLE-A", DeviceConnectionState.Disconnected)];
        var cut = Render<DeviceSwitcher>();
        Assert.Equal("disconnected", cut.Find("[data-testid=connection-state]").GetAttribute("data-state"));

        _directory.Push([Deck("EXAMPLE-A")]);

        cut.WaitForAssertion(() =>
            Assert.Equal("connected", cut.Find("[data-testid=connection-state]").GetAttribute("data-state")));
    }

    [Fact]
    public async Task Components_StopListening_WhenDisposed()
    {
        _directory.Devices = [Deck("EXAMPLE-A")];
        var home = Render<Home>();
        var switcher = Render<DeviceSwitcher>();
        Assert.Equal(2, _directory.Listeners);

        await DisposeComponentsAsync();

        Assert.Equal(0, _directory.Listeners);
    }

    private sealed class FakeDeviceDirectory : IDeviceDirectory
    {
        private Action? _changed;

        public IReadOnlyList<DeviceSummary> Devices { get; set; } = [];

        public int Listeners => _changed?.GetInvocationList().Length ?? 0;

        public event Action? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public Task<IReadOnlyList<DeviceSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Devices);

        /// <summary>What the live state does when an event arrives: new data, then Changed (off the renderer's thread).</summary>
        public void Push(IReadOnlyList<DeviceSummary> devices)
        {
            Devices = devices;
            Task.Run(() => _changed?.Invoke()).Wait();
        }
    }
}
