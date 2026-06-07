using System.Text.Json;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Tests.Serialisation;

public class SerialisationTests
{
    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions.Default), JsonOptions.Default)!;

    [Fact]
    public void DeviceEntry_RoundTrips()
    {
        var entry = new DeviceEntry("SN001", "Stream Deck MK.2", 3, 5,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(entry, RoundTrip(entry));
    }

    [Fact]
    public void DeviceCatalogue_RoundTrips()
    {
        var catalogue = new DeviceCatalogue(1, [
            new DeviceEntry("SN001", "Stream Deck MK.2", 3, 5,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        // Assert.Equivalent does deep structural comparison, handling IReadOnlyList<T>
        // whose concrete type changes after JSON round-trip (ReadOnlySingleElementList -> List<T>).
        Assert.Equivalent(catalogue, RoundTrip(catalogue));
    }

    [Fact]
    public void ButtonAction_PublishAction_RoundTrips()
    {
        ButtonAction action = new PublishAction("home/light/set", "ON");
        var result = RoundTrip(action);
        var publish = Assert.IsType<PublishAction>(result);
        Assert.Equal("home/light/set", publish.Topic);
        Assert.Equal("ON", publish.Payload);
    }

    [Fact]
    public void ButtonAction_NavigateAction_RoundTrips()
    {
        ButtonAction action = new NavigateAction("page-2");
        var result = RoundTrip(action);
        var navigate = Assert.IsType<NavigateAction>(result);
        Assert.Equal("page-2", navigate.TargetPageId);
    }

    [Fact]
    public void ButtonGridPage_RoundTrips()
    {
        var page = new ButtonGridPage("main", [
            new ButtonDefinition(
                ButtonId: "co2-sensor",
                KeyIndex: 0,
                PageId: "main",
                Display: new DisplaySpec("builtin:co2", IconPlacement.Corner,
                    Center: new TextZone(null, "{value} {unit}"), Bottom: new TextZone("CO2", null)),
                Inbound: new InboundBinding("home/sensor/co2", "value", "unit", true, TimeSpan.FromSeconds(30)),
                Rules: [new ConditionalRule(">1000", "#FF0000", null)],
                Gestures: new Dictionary<string, IReadOnlyList<ButtonAction>>
                {
                    ["Tap"] = [new PublishAction("home/ventilation/toggle", "true")]
                })
        ]);

        var result = RoundTrip(page);
        var grid = Assert.IsType<ButtonGridPage>(result);
        Assert.Single(grid.Buttons);
        Assert.Equal("co2-sensor", grid.Buttons[0].ButtonId);
        Assert.IsType<PublishAction>(grid.Buttons[0].Gestures["Tap"][0]);
    }

    [Fact]
    public void DeviceConfig_WithMultiplePageTypes_RoundTrips()
    {
        var config = new DeviceConfig(1, "SN001", [
            new ButtonGridPage("main", []),
        ]);
        var result = RoundTrip(config);
        Assert.Equal("SN001", result.Serial);
        Assert.IsType<ButtonGridPage>(result.Pages[0]);
    }

    [Fact]
    public void ButtonDefinition_WithBothActionTypes_RoundTrips()
    {
        var button = new ButtonDefinition(
            ButtonId: "nav-button",
            KeyIndex: 4,
            PageId: "main",
            Display: new DisplaySpec(null, IconPlacement.Center, Bottom: new TextZone("Menu", null)),
            Inbound: null,
            Rules: [],
            Gestures: new Dictionary<string, IReadOnlyList<ButtonAction>>
            {
                ["Tap"] = [
                    new PublishAction("home/event", "pressed"),
                    new NavigateAction("menu-page")
                ]
            });

        var result = RoundTrip(button);
        Assert.Equal(2, result.Gestures["Tap"].Count);
        Assert.IsType<PublishAction>(result.Gestures["Tap"][0]);
        Assert.IsType<NavigateAction>(result.Gestures["Tap"][1]);
    }
}
