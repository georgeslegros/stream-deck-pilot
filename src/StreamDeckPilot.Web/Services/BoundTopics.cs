using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Events;

namespace StreamDeckPilot.Web.Services;

/// <summary>
/// One MQTT topic a deck listens to, for the dashboard (#99): which button or control reads it,
/// its latest payload (null until one arrived since the service started) and whether the deck
/// shows it as stale.
/// </summary>
public sealed record BoundTopic(string Owner, string PageId, string Role, string Topic, TopicValue? Latest, bool Stale);

public static class BoundTopics
{
    /// <summary>
    /// Every bound topic of <paramref name="config"/>, page by page. Staleness comes from
    /// <paramref name="isDimmed"/> (desired state), so the list agrees with the keys: a topic is
    /// stale when it had a value and the key shows the dimmed look.
    /// </summary>
    public static IReadOnlyList<BoundTopic> From(
        DeviceConfig config, int columns, LiveSnapshot snapshot, Func<string, int, bool> isDimmed)
    {
        var list = new List<BoundTopic>();
        foreach (var page in config.Pages.OfType<ButtonGridPage>())
        {
            foreach (var button in page.Buttons.OrderBy(b => b.KeyIndex))
            {
                if (button.Inbound is not { } inbound || string.IsNullOrWhiteSpace(inbound.Topic)) continue;
                var latest = Latest(snapshot, inbound.Topic);
                list.Add(new BoundTopic(button.ButtonId, page.PageId, "Value", inbound.Topic, latest,
                    latest is not null && isDimmed(page.PageId, button.KeyIndex)));
            }

            foreach (var control in (page.Controls ?? []).OfType<SetpointControl>())
            {
                var current = Latest(snapshot, control.Current.Topic);
                list.Add(new BoundTopic(control.ControlId, page.PageId, "Current", control.Current.Topic, current,
                    current is not null && isDimmed(page.PageId, control.Keys(columns).Value)));
                // The target has no staleness timeout: it only changes when someone sets it.
                list.Add(new BoundTopic(control.ControlId, page.PageId, "Target", control.Target.Topic,
                    Latest(snapshot, control.Target.Topic), Stale: false));
            }
        }
        return list;
    }

    private static TopicValue? Latest(LiveSnapshot snapshot, string topic) =>
        snapshot.Values.TryGetValue(topic, out var value) ? value : null;
}
