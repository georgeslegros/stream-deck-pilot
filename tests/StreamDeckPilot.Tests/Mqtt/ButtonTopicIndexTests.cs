using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Mqtt;

namespace StreamDeckPilot.Tests.Mqtt;

public sealed class ButtonTopicIndexTests
{
    private static DeviceConfig Config(string serial, string topic) => new(1, serial, [
        new ButtonGridPage("main", [
            new ButtonDefinition("b0", 0, "main",
                new DisplaySpec(null, IconPlacement.Corner, new TextZone(null, "{value}"), new TextZone("L", null)),
                new InboundBinding(topic, "value", "unit", true, null),
                [],
                new Dictionary<string, IReadOnlyList<ButtonAction>>())
        ])
    ]);

    [Fact]
    public void TopicsFor_ReturnsOnlyThatDevicesTopics()
    {
        var index = new ButtonTopicIndex();
        index.Update("SN1", Config("SN1", "home/co2"));
        index.Update("SN2", Config("SN2", "home/humidity"));

        Assert.Equal(["home/co2"], index.TopicsFor("SN1"));
        Assert.Empty(index.TopicsFor("SN3"));
    }

    // The retained refresh subscribes then unsubscribes each topic: a wildcard binding must never
    // be in that list (unsubscribing "home/#" would drop the service's own wildcard), nor a topic
    // the wildcard does not cover.
    [Fact]
    public void RetainedRefreshTopics_ExactTopicsUnderPrefixOnly()
    {
        var topics = MqttClientService.RetainedRefreshTopics(
            ["home/co2", "home/#", "home/+/temp", "office/co2", "home/a/b"], "home/#");

        Assert.Equal(["home/co2", "home/a/b"], topics);
    }

    [Fact]
    public void Update_RemovingSerial_DropsEmptyTopicKeys()
    {
        var index = new ButtonTopicIndex();
        index.Update("SN1", Config("SN1", "home/co2"));
        Assert.Contains("home/co2", index.AllTopics);

        index.Update("SN1", null); // device config removed
        Assert.DoesNotContain("home/co2", index.AllTopics);
        Assert.Empty(index.Lookup("home/co2"));
    }

    [Fact]
    public async Task Update_DoesNotMutateListsObservedByConcurrentLookup()
    {
        // Regression: a shallow dictionary copy shared List<Entry> instances with the live index, so
        // Update's RemoveAll/Add mutated lists that Lookup was enumerating → "Collection was modified".
        // This hammers Update and Lookup concurrently; it must never throw.
        var index = new ButtonTopicIndex();
        index.Update("SN1", Config("SN1", "home/co2"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Exception? failure = null;

        var reader = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                    foreach (var _ in index.Lookup("home/co2")) { /* enumerate */ }
            }
            catch (Exception ex) { failure = ex; }
        });

        var writer = Task.Run(() =>
        {
            var toggle = false;
            while (!cts.IsCancellationRequested)
            {
                index.Update("SN1", toggle ? Config("SN1", "home/co2") : null);
                toggle = !toggle;
            }
        });

        await Task.WhenAll(reader, writer);
        Assert.Null(failure);
    }
}
