using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;

namespace StreamDeckPilot.Infrastructure.Events;

/// <summary>
/// Turns the runtime's own change notifications into bus events (#108), so the device runtime,
/// MQTT client and renderer don't know about live clients. Every handler only calls
/// <see cref="IEventBus.Publish"/>, which never blocks: KeyRendered comes straight off the render
/// path. Config events are published by the config endpoint, which knows the outcome.
/// Registered before the device supervisor so the first connection is already seen.
/// </summary>
public sealed class LiveEventBridge(
    IEventBus bus,
    IOptions<EventsOptions> options,
    DeviceSupervisorService supervisor,
    ActivePageStore pages,
    PaintedFrameStore frames,
    IBrokerSession broker,
    BrokerIndicator indicator,
    BrightnessController brightness,
    LatestValues values,
    TimeProvider? time = null) : IHostedService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly int _maxValueLength = Math.Max(16, options.Value.MaxValueLength);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        supervisor.ConnectionChanged += OnConnectionChanged;
        pages.Changed += OnPageChanged;
        frames.Painted += OnPainted;
        broker.SessionChanged += OnSessionChanged;
        broker.ValueReceived += OnValueReceived;
        indicator.Changed += OnIndicatorChanged;
        brightness.EffectiveChanged += OnBrightnessChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        supervisor.ConnectionChanged -= OnConnectionChanged;
        pages.Changed -= OnPageChanged;
        frames.Painted -= OnPainted;
        broker.SessionChanged -= OnSessionChanged;
        broker.ValueReceived -= OnValueReceived;
        indicator.Changed -= OnIndicatorChanged;
        brightness.EffectiveChanged -= OnBrightnessChanged;
        return Task.CompletedTask;
    }

    private void OnConnectionChanged(string serial, DeviceConnectionState state) =>
        bus.Publish(state == DeviceConnectionState.Connected
            ? new DeviceConnected { Serial = serial }
            : new DeviceDisconnected { Serial = serial });

    private void OnPageChanged(string serial, string pageId) =>
        bus.Publish(new PageChanged(pageId) { Serial = serial });

    private void OnPainted(string serial, int key, PaintedFrame frame) =>
        bus.Publish(new KeyRendered(key, frame.Hash) { Serial = serial });

    private void OnSessionChanged(bool up) =>
        bus.Publish(up ? new BrokerOnline() : new BrokerOffline(indicator.ShowOffline));

    // The offline look turning on is news; turning off is covered by BrokerOnline.
    private void OnIndicatorChanged(bool showOffline)
    {
        if (showOffline) bus.Publish(new BrokerOffline(OfflineIndicator: true));
    }

    private void OnBrightnessChanged(int level) => bus.Publish(new BrightnessChanged(level));

    private void OnValueReceived(string topic, string payload)
    {
        var value = payload.Length > _maxValueLength ? payload[.._maxValueLength] : payload;
        values.Set(topic, new TopicValue(value, _time.GetUtcNow()));
        bus.Publish(new ValueReceived(topic, value));
    }
}
