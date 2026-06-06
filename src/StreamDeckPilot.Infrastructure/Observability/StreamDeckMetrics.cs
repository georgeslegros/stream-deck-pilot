using System.Diagnostics.Metrics;
using StreamDeckPilot.Core.DeviceState;

namespace StreamDeckPilot.Infrastructure.Observability;

public sealed class StreamDeckMetrics : IDisposable
{
    private readonly Meter _meter = new("StreamDeckPilot", "1.0");

    public Counter<long> MqttMessagesReceived { get; }
    public Counter<long> MqttMessagesDropped  { get; }
    public Counter<long> RenderOperations     { get; }
    public Counter<long> RenderFailures       { get; }
    public Counter<long> ButtonPresses        { get; }
    public Counter<long> DeviceReconnects     { get; }

    public StreamDeckMetrics()
    {
        MqttMessagesReceived = _meter.CreateCounter<long>("streamdeck.mqtt.messages_received", "messages");
        MqttMessagesDropped  = _meter.CreateCounter<long>("streamdeck.mqtt.messages_dropped",  "messages");
        RenderOperations     = _meter.CreateCounter<long>("streamdeck.render.operations");
        RenderFailures       = _meter.CreateCounter<long>("streamdeck.render.failures");
        ButtonPresses        = _meter.CreateCounter<long>("streamdeck.button.presses");
        DeviceReconnects     = _meter.CreateCounter<long>("streamdeck.device.reconnects");
    }

    // Called from Program.cs after all services are registered, using lazy service-locator lambdas
    // to avoid circular DI dependencies between Metrics ↔ Supervisor and Metrics ↔ MqttClientService.
    public void RegisterObservableGauges(
        Func<IReadOnlyDictionary<string, DeviceConnectionState>> getDeviceStates,
        Func<bool> getBrokerConnected)
    {
        _meter.CreateObservableGauge("streamdeck.device.connection_state",
            () => getDeviceStates().Select(kv =>
                new Measurement<int>((int)kv.Value,
                    [new KeyValuePair<string, object?>("serial", kv.Key)])));

        _meter.CreateObservableGauge("streamdeck.broker.connected",
            () => new[] { new Measurement<int>(getBrokerConnected() ? 1 : 0) });
    }

    public void Dispose() => _meter.Dispose();
}
