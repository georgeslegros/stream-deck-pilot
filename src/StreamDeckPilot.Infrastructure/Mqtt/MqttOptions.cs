namespace StreamDeckPilot.Infrastructure.Mqtt;

public record MqttOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 1883;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string ClientId { get; init; } = "stream-deck-pilot";

    /// <summary>
    /// Single wildcard subscription covering every button-bound topic. The internal
    /// <see cref="ButtonTopicIndex"/> dispatches exact-topic matches and silently drops
    /// anything not in the index. Changing button config never touches the broker.
    /// </summary>
    public string TopicPrefix { get; init; } = "home/#";

    /// <summary>Upper bound for reconnect backoff. Loop never gives up.</summary>
    public int MaxReconnectDelaySeconds { get; init; } = 30;

    /// <summary>
    /// Subscription-liveness watchdog. TryPingAsync only proves the TCP/MQTT transport is alive;
    /// it cannot detect a broker-side subscription loss (RabbitMQ MQTT plugin restart, queue
    /// deletion, permission change, session take-over) that leaves the socket up but silently deaf.
    /// If no inbound message arrives within this window the loop forces a reconnect+resubscribe so
    /// the wildcard subscription is re-established. Set to 0 to disable (e.g. genuinely silent setups).
    /// Default 0 (disabled) because a quiet topic tree is normal; raise it for chatty deployments
    /// where prolonged silence reliably indicates a lost subscription rather than no traffic.
    /// </summary>
    public int InboundSilenceReconnectSeconds { get; init; }
}
