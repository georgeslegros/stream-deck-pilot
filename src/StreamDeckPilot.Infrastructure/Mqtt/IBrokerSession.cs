namespace StreamDeckPilot.Infrastructure.Mqtt;

/// <summary>The MQTT session as live clients see it (#108): up = connected and subscribed.</summary>
public interface IBrokerSession
{
    bool IsSessionUp { get; }

    /// <summary>Raised with true when the session is established, false when it's lost.</summary>
    event Action<bool>? SessionChanged;

    /// <summary>Raised with (topic, payload) after a message on a bound topic was processed.</summary>
    event Action<string, string>? ValueReceived;
}
