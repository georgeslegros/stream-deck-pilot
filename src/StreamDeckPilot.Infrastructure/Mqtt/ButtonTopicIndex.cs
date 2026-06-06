using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Infrastructure.Mqtt;

public sealed class ButtonTopicIndex
{
    private record Entry(string Serial, string PageId, ButtonDefinition Button);

    private volatile Dictionary<string, List<Entry>> _index = new(StringComparer.Ordinal);

    public async Task RebuildAsync(ConfigStore configStore)
    {
        var serials = await configStore.ListSerialsAsync();
        var next = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var serial in serials)
        {
            var config = await configStore.LoadAsync(serial);
            if (config is not null) AddConfig(next, serial, config);
        }
        _index = next;
    }

    public void Update(string serial, DeviceConfig? config)
    {
        // COPY-ON-WRITE at the LIST level. A shallow Dictionary copy would share the same List<Entry>
        // instances with the currently-published _index; RemoveAll/Add would then mutate lists that
        // Lookup() is concurrently enumerating on the inbound drainer thread → "Collection was
        // modified" / torn reads (Update runs on the API thread via NotifyConfigChangedAsync). Deep-copy
        // each value list so we only ever mutate fresh, unpublished lists. The volatile reference swap
        // then publishes an immutable snapshot — matching RebuildAsync's all-fresh pattern.
        var next = _index.ToDictionary(
            kv => kv.Key,
            kv => new List<Entry>(kv.Value),
            StringComparer.Ordinal);
        foreach (var list in next.Values)
            list.RemoveAll(e => e.Serial == serial);
        if (config is not null) AddConfig(next, serial, config);
        // Drop keys whose list emptied out so AllTopics stays accurate.
        foreach (var key in next.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            next.Remove(key);
        _index = next;
    }

    public IReadOnlyList<string> AllTopics => _index.Keys.ToList();

    public IReadOnlyList<(string Serial, string PageId, ButtonDefinition Button)> Lookup(string topic) =>
        _index.TryGetValue(topic, out var entries)
            ? entries.Select(e => (e.Serial, e.PageId, e.Button)).ToList()
            : [];

    private static void AddConfig(Dictionary<string, List<Entry>> index, string serial, DeviceConfig config)
    {
        foreach (var page in config.Pages)
        {
            if (page is not ButtonGridPage grid) continue;
            foreach (var button in grid.Buttons)
            {
                if (button.Inbound?.Topic is not { } topic) continue;
                if (!index.TryGetValue(topic, out var list))
                    index[topic] = list = [];
                list.Add(new Entry(serial, page.PageId, button));
            }
        }
    }
}
