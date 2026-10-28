using System.Text.Json.Serialization;
using StreamDeckPilot.Core.DeviceState;

namespace StreamDeckPilot.Infrastructure.Events;

/// <summary>
/// Something that happened in the service, pushed to live clients (#108): the web UI's circuits,
/// the SSE stream (<c>/api/events</c>) and the SignalR hub (<c>/hubs/live</c>). Events are deltas
/// against a <see cref="LiveSnapshot"/>. The hierarchy is open: later sources (profiles)
/// add their own records and a line in <see cref="LiveEventTypes"/>.
/// </summary>
public abstract record LiveEvent
{
    /// <summary>The event's name on the wire (<c>event:</c> in SSE, <c>type</c> in JSON).</summary>
    public string Type => GetType().Name;

    /// <summary>Position in the bus's sequence, set when the event is delivered. Increases by one per delivered event.</summary>
    public long Sequence { get; init; }

    /// <summary>When the event was delivered.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>The deck it concerns; null for service-wide events (broker, brightness, MQTT values).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Serial { get; init; }

    /// <summary>
    /// Events with the same key are coalesced: latest wins, at most <c>Events:MaxPerSecondPerKey</c>
    /// per key, so a chatty topic or a blinking key can't flood a phone. Null = never coalesced.
    /// </summary>
    [JsonIgnore]
    public virtual string? CoalesceKey => null;
}

/// <summary>A deck's connection state changed to <see cref="DeviceConnectionState.Connected"/>.</summary>
public sealed record DeviceConnected : LiveEvent;

/// <summary>A deck's connection state changed away from <see cref="DeviceConnectionState.Connected"/>.</summary>
public sealed record DeviceDisconnected : LiveEvent;

/// <summary>A key now shows a different image: fetch <c>/devices/{serial}/keys/{key}.png</c> when <see cref="Hash"/> changed.</summary>
public sealed record KeyRendered(int Key, string Hash) : LiveEvent
{
    public override string? CoalesceKey => $"key:{Serial}:{Key}";
}

/// <summary>A message arrived on a topic that a button or control is bound to.</summary>
public sealed record ValueReceived(string Topic, string Value) : LiveEvent
{
    public override string? CoalesceKey => $"value:{Topic}";
}

/// <summary>The MQTT session is up (connected and subscribed).</summary>
public sealed record BrokerOnline : LiveEvent;

/// <summary>
/// The MQTT session is down; the service keeps retrying. Sent when it drops
/// (<see cref="OfflineIndicator"/> false), and again once the deck shows the offline look, after
/// <c>Mqtt:OfflineIndicatorDelaySeconds</c> (true).
/// </summary>
public sealed record BrokerOffline(bool OfflineIndicator) : LiveEvent;

/// <summary>A deck's config was saved and applied.</summary>
public sealed record ConfigChanged : LiveEvent;

/// <summary>A config upload for a deck was refused; nothing changed.</summary>
public sealed record ConfigRejected(IReadOnlyList<string> Errors) : LiveEvent;

/// <summary>A deck now shows another page.</summary>
public sealed record PageChanged(string PageId) : LiveEvent;

/// <summary>The brightness the decks run at changed (0–100). Service-wide.</summary>
public sealed record BrightnessChanged(int Level) : LiveEvent;

/// <summary>
/// A sign-in or first-run setup attempt on the web UI failed (#97). Service-wide. Carries no user
/// name, password or token: only how the attempt was made and why it was refused. The client
/// address is in the audit log, not here.
/// </summary>
/// <param name="Method"><c>local</c>, <c>setup</c> or <c>oidc:&lt;provider&gt;</c>.</param>
/// <param name="Reason"><c>invalid_credentials</c>, <c>rate_limited</c>, <c>invalid_api_key</c> (setup), <c>accounts_unreadable</c>, <c>no_role</c> or <c>remote_failure</c>.</param>
public sealed record SignInFailed(string Method, string Reason) : LiveEvent;

/// <summary>
/// An admin created a named API key (#98). Service-wide. Never carries the key or its hash: only the
/// id (the public part of the key), name, scope and expiry.
/// </summary>
/// <param name="Scope"><c>ReadOnly</c> or <c>Full</c>.</param>
public sealed record ApiKeyCreated(string Id, string Name, string Scope, DateTimeOffset? ExpiresAt) : LiveEvent;

/// <summary>An admin revoked a named API key (#98): requests with it now get 401 and its open streams end.</summary>
public sealed record ApiKeyRevoked(string Id, string Name) : LiveEvent;

/// <summary>The event names clients can filter on, and whether each one concerns a single deck.</summary>
public static class LiveEventTypes
{
    public static IReadOnlyDictionary<string, bool> PerDevice { get; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(DeviceConnected)] = true,
            [nameof(DeviceDisconnected)] = true,
            [nameof(KeyRendered)] = true,
            [nameof(ValueReceived)] = false,
            [nameof(BrokerOnline)] = false,
            [nameof(BrokerOffline)] = false,
            [nameof(ConfigChanged)] = true,
            [nameof(ConfigRejected)] = true,
            [nameof(PageChanged)] = true,
            [nameof(BrightnessChanged)] = false,
            [nameof(SignInFailed)] = false,
            [nameof(ApiKeyCreated)] = false,
            [nameof(ApiKeyRevoked)] = false,
        };

    public static IEnumerable<string> All => PerDevice.Keys;

    /// <summary>The canonical spelling of a type name, or null if there is no such event.</summary>
    public static string? Canonical(string name) =>
        PerDevice.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
}
