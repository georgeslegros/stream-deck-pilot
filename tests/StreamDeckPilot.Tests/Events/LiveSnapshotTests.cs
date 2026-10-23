using System.Collections.Immutable;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Infrastructure.Events;

namespace StreamDeckPilot.Tests.Events;

/// <summary>Folding live events into a snapshot (#108).</summary>
public sealed class LiveSnapshotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static LiveSnapshot Snapshot() => new(
        "boot", 10,
        new BrokerSnapshot(false, false, null),
        80,
        [new DeviceSnapshot("A", "Stream Deck MK.2", 3, 5, DeviceConnectionState.Disconnected, "main", ImmutableDictionary<int, string>.Empty)],
        ImmutableDictionary<string, TopicValue>.Empty);

    [Fact]
    public void DeviceEvents_UpdateTheirDeck()
    {
        var s = Snapshot()
            .Apply(new DeviceConnected { Serial = "A", Sequence = 11 })!
            .Apply(new PageChanged("climate") { Serial = "A", Sequence = 12 })!
            .Apply(new KeyRendered(3, "abc") { Serial = "A", Sequence = 13 })!;

        var device = s.Device("A")!;
        Assert.True(device.IsConnected);
        Assert.Equal("climate", device.ActivePage);
        Assert.Equal("abc", device.Keys[3]);
        Assert.Equal(13, s.Sequence);

        Assert.Equal(DeviceConnectionState.Disconnected,
            s.Apply(new DeviceDisconnected { Serial = "A", Sequence = 14 })!.Device("A")!.State);
    }

    [Fact]
    public void AnEventForAnUnknownDeck_AsksForAReload()
    {
        Assert.Null(Snapshot().Apply(new DeviceConnected { Serial = "NEW", Sequence = 11 }));
    }

    [Fact]
    public void Broker_Brightness_AndValues()
    {
        var s = Snapshot()
            .Apply(new BrokerOnline { Sequence = 11 })!
            .Apply(new ValueReceived("sensors/co2", "800") { Sequence = 12, At = T0 })!
            .Apply(new BrightnessChanged(30) { Sequence = 13 })!;

        Assert.True(s.Broker.Connected);
        Assert.Equal(T0, s.Broker.LastMessageAt);
        Assert.Equal(new TopicValue("800", T0), s.Values["sensors/co2"]);
        Assert.Equal(30, s.Brightness);

        var offline = s.Apply(new BrokerOffline(OfflineIndicator: true) { Sequence = 14 })!;
        Assert.False(offline.Broker.Connected);
        Assert.True(offline.Broker.OfflineIndicator);
        Assert.False(offline.Apply(new BrokerOnline { Sequence = 15 })!.Broker.OfflineIndicator);
    }

    [Fact]
    public void ConfigEvents_LeaveTheStateAsIs()
    {
        var s = Snapshot();
        var after = s.Apply(new ConfigRejected(["bad"]) { Serial = "A", Sequence = 11 })!;
        Assert.Equal(s with { Sequence = 11 }, after);
    }
}
