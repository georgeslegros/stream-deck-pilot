using System.Collections.Immutable;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Web.Components.Dashboard;
using StreamDeckPilot.Web.Services;

namespace StreamDeckPilot.Tests.Web;

/// <summary>bUnit tests for the read-only dashboard (#99): cards, bound topics and staleness, live key images.</summary>
public sealed class DashboardComponentTests : BunitContext
{
    private const string Serial = "EXAMPLE-A";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, IReadOnlyList<ButtonAction>> NoGestures = new();

    private readonly FakeDashboardSource _source = new();

    public DashboardComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IDashboardSource>(_source);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        Services.AddScoped<BrowserClock>();
    }

    private static ButtonDefinition Bound(string id, int key, string topic, string page = "main") =>
        new(id, key, page, new DisplaySpec(), new InboundBinding(topic, null, null, false, TimeSpan.FromMinutes(5)), [], NoGestures);

    private static DeviceConfig Config(params Page[] pages) => new(2, Serial, pages);

    private LiveSnapshot WithValue(LiveSnapshot snapshot, string topic, string value, DateTimeOffset at) =>
        snapshot with { Values = snapshot.Values.SetItem(topic, new TopicValue(value, at)) };

    // ── Cards ───────────────────────────────────────────────────────────────

    [Fact]
    public void DeviceCard_ShowsStateModelBrightnessPage_AndLinksToTheDeck()
    {
        var cut = Render<DeviceCard>(p => p
            .Add(c => c.Device, FakeDashboardSource.Deck(Serial, activePage: "climate"))
            .Add(c => c.Brightness, 60));

        Assert.Equal("connected", cut.Find("[data-testid=deck-state]").GetAttribute("data-state"));
        Assert.Contains("Stream Deck MK.2 · 5 × 3 keys", cut.Markup);
        Assert.Contains("Brightness 60 %", cut.Find("[data-testid=deck-brightness]").TextContent);
        Assert.Contains("climate", cut.Find("[data-testid=deck-page]").TextContent);
        Assert.Equal($"board/{Serial}", cut.Find("[data-testid=deck-link]").GetAttribute("href"));
    }

    [Fact]
    public void BrokerCard_Offline_ShowsTheIndicatorState_AndTheLastMessageAge()
    {
        var cut = Render<BrokerCard>(p => p.Add(c => c.Broker,
            new BrokerSnapshot(false, true, Now.AddMinutes(-2))));

        Assert.Equal("offline", cut.Find("[data-testid=broker-state]").GetAttribute("data-state"));
        Assert.Contains("Decks show the offline look.", cut.Find("[data-testid=broker-indicator]").TextContent);
        var last = cut.Find("[data-testid=broker-last-message]").TextContent;
        Assert.Contains("11:58:00", last);
        Assert.Contains("2 min ago", last);
    }

    [Fact]
    public void BrokerCard_NoMessageYet_SaysSo()
    {
        var cut = Render<BrokerCard>(p => p.Add(c => c.Broker, new BrokerSnapshot(true, false, null)));
        Assert.Contains("No message since the service started", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid=broker-indicator]"));
    }

    // ── Bound topics and staleness ──────────────────────────────────────────

    [Fact]
    public void Topics_ShowValueAndAge_AndHighlightOnlyStaleOnes()
    {
        var config = Config(new ButtonGridPage("main",
            [Bound("co2", 0, "sensors/co2"), Bound("temp", 1, "sensors/temp"), Bound("door", 2, "sensors/door")]));
        var snapshot = WithValue(WithValue(FakeDashboardSource.Snap(FakeDashboardSource.Deck(Serial)),
            "sensors/co2", "850", Now.AddSeconds(-30)), "sensors/temp", "21.5", Now.AddHours(-2));
        _source.Dimmed.Add((Serial, "main", 1));   // the renderer dims temp: stale
        _source.Dimmed.Add((Serial, "main", 2));   // door is dimmed too, but has no value: waiting, not stale

        var topics = BoundTopics.From(config, 5, snapshot, (page, key) => _source.IsDimmed(Serial, page, key));
        var cut = Render<BoundTopicsList>(p => p.Add(c => c.Topics, topics));

        var rows = cut.FindAll("[data-testid=bound-topic]");
        Assert.Equal(3, rows.Count);
        Assert.Equal("false", rows[0].GetAttribute("data-stale"));
        Assert.Contains("850", rows[0].TextContent);
        Assert.Contains("30 s ago", rows[0].TextContent);
        Assert.Equal("true", rows[1].GetAttribute("data-stale"));
        Assert.Contains("sdp-topic-stale", rows[1].ClassName);
        Assert.Contains("2 h ago", rows[1].TextContent);
        Assert.Equal("false", rows[2].GetAttribute("data-stale"));
        Assert.Contains("No value since the service started", rows[2].TextContent);
        Assert.Single(cut.FindAll("[data-testid=stale-chip]"));
    }

    [Fact]
    public void Topics_SetpointControl_ListsCurrentAndTarget_StaleFromItsValueKey()
    {
        // Vertical at anchor 1 on a 5-wide grid: + is key 1, the value key 6, − key 11.
        var control = new SetpointControl("office", 1, SetpointOrientation.Vertical,
            new SetpointBinding("climate/office/current", StalenessTimeout: TimeSpan.FromMinutes(10)),
            new SetpointBinding("climate/office/target"), new SetpointCommit("climate/office/set"), 0.5, 5, 30);
        var config = Config(new ButtonGridPage("climate", [], [control]));
        var snapshot = WithValue(WithValue(FakeDashboardSource.Snap(FakeDashboardSource.Deck(Serial)),
            "climate/office/current", "20.5", Now.AddMinutes(-20)), "climate/office/target", "21", Now.AddMinutes(-20));
        _source.Dimmed.Add((Serial, "climate", 6));

        var topics = BoundTopics.From(config, 5, snapshot, (page, key) => _source.IsDimmed(Serial, page, key));

        Assert.Equal(2, topics.Count);
        Assert.Equal(("Current", "climate/office/current", true), (topics[0].Role, topics[0].Topic, topics[0].Stale));
        Assert.Equal(("Target", "climate/office/target", false), (topics[1].Role, topics[1].Topic, topics[1].Stale));
    }

    [Fact]
    public void DeviceDashboard_StalenessFlip_IsPushed_WithoutALiveEvent()
    {
        _source.Configs[Serial] = Config(new ButtonGridPage("main", [Bound("co2", 0, "sensors/co2")]));
        var snapshot = WithValue(FakeDashboardSource.Snap(FakeDashboardSource.Deck(Serial)), "sensors/co2", "850", Now.AddMinutes(-6));
        _source.Snapshot = snapshot;
        var cut = Render<DeviceDashboard>(p => p
            .Add(c => c.Device, snapshot.Devices[0])
            .Add(c => c.Snapshot, snapshot));
        Assert.Equal("false", cut.Find("[data-testid=bound-topic]").GetAttribute("data-stale"));

        // The staleness monitor dims the key: a desired-state write, no bus event.
        _source.Dimmed.Add((Serial, "main", 0));
        _source.PushDesiredState(Serial);

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=bound-topic]").GetAttribute("data-stale")));
    }

    // ── Live key images ─────────────────────────────────────────────────────

    private IRenderedComponent<LiveBoard> RenderBoard()
    {
        _source.Snapshot = FakeDashboardSource.Snap(FakeDashboardSource.Deck(Serial));
        return Render<LiveBoard>(p => p.Add(c => c.Serial, Serial).Add(c => c.PageId, "main"));
    }

    private static string? ETagOf(IRenderedComponent<LiveBoard> cut, int key) =>
        cut.Find($"[data-testid=board-key][data-key='{key}']").GetAttribute("data-etag");

    [Fact]
    public void LiveBoard_ShowsEveryKey_AsAnInlineImage()
    {
        var cut = RenderBoard();

        var keys = cut.FindAll("img[data-testid=board-key]");
        Assert.Equal(15, keys.Count);
        Assert.All(keys, k => Assert.StartsWith("data:image/png;base64,", k.GetAttribute("src")));
    }

    [Fact]
    public void LiveBoard_KeyRendered_SwapsOnlyThatKey()
    {
        var cut = RenderBoard();
        var before = Enumerable.Range(0, 15).Select(k => ETagOf(cut, k)).ToList();
        _source.KeyImageRequests.Clear();

        _source.KeyVersions[(Serial, 3)] = 1;
        _source.Push(new KeyRendered(3, "hash-1") { Serial = Serial, Sequence = 1 });

        cut.WaitForAssertion(() => Assert.Equal($"{Serial}-3-v1", ETagOf(cut, 3)));
        Assert.Equal([(Serial, 3)], _source.KeyImageRequests);
        for (var k = 0; k < 15; k++)
            if (k != 3) Assert.Equal(before[k], ETagOf(cut, k));
    }

    [Fact]
    public void LiveBoard_SameHashAgain_FetchesNothing()
    {
        var cut = RenderBoard();
        _source.Push(new KeyRendered(3, "hash-1") { Serial = Serial, Sequence = 1 });
        cut.WaitForAssertion(() => Assert.Contains((Serial, 3), _source.KeyImageRequests.Skip(15)));
        _source.KeyImageRequests.Clear();

        _source.Push(new KeyRendered(3, "hash-1") { Serial = Serial, Sequence = 2 });
        _source.Push(new KeyRendered(4, "other") { Serial = "EXAMPLE-B", Sequence = 3 });   // another deck
        _source.Push(new KeyRendered(5, "hash-5") { Serial = Serial, Sequence = 4 });       // a marker: handled after the two above

        cut.WaitForAssertion(() => Assert.Contains((Serial, 5), _source.KeyImageRequests));
        Assert.Equal([(Serial, 5)], _source.KeyImageRequests);
    }

    [Fact]
    public void LiveBoard_DisconnectedDeck_FollowsDesiredState_ConnectedDeckWaitsForKeyRendered()
    {
        var cut = RenderBoard();
        _source.KeyImageRequests.Clear();

        _source.PushDesiredState(Serial);   // connected: the painted frames will follow via KeyRendered
        Assert.Empty(_source.KeyImageRequests);

        _source.Snapshot = FakeDashboardSource.Snap(FakeDashboardSource.Deck(Serial, DeviceConnectionState.Disconnected));
        _source.KeyVersions[(Serial, 0)] = 2;
        _source.PushDesiredState(Serial);

        cut.WaitForAssertion(() => Assert.Equal($"{Serial}-0-v2", ETagOf(cut, 0)));
    }

    // ── Other pages ─────────────────────────────────────────────────────────

    [Fact]
    public void PagePreviews_AreCollapsed_AndRenderedOnlyWhenOpened()
    {
        _source.Snapshot = FakeDashboardSource.Snap(FakeDashboardSource.Deck(Serial));
        var cut = Render<PagePreviews>(p => p.Add(c => c.Serial, Serial).Add(c => c.Pages, ["climate", "lights"]));

        Assert.Equal(2, cut.FindAll("[data-testid=page-panel]").Count);
        Assert.Empty(_source.PageImageRequests);
        _source.PushDesiredState(Serial);   // nothing open: nothing rendered
        Assert.Empty(_source.PageImageRequests);

        cut.FindAll(".mud-expand-panel-header")[0].Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=page-image]")));
        Assert.Equal([(Serial, "climate")], _source.PageImageRequests);

        _source.PushDesiredState(Serial);   // an open page follows desired state
        cut.WaitForAssertion(() => Assert.Equal(2, _source.PageImageRequests.Count));
        Assert.All(_source.PageImageRequests, r => Assert.Equal("climate", r.PageId));
    }
}
