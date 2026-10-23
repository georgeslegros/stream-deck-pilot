using System.Text.Json.Serialization;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Infrastructure.Mqtt;

namespace StreamDeckPilot.Api.Events;

/// <summary>Live events (#108): the bus, its bridge from the runtime, SSE and the SignalR hub.</summary>
public static class LiveEventsExtensions
{
    /// <summary>
    /// Call before the device supervisor's hosted service is registered: the bridge must be
    /// listening before the first deck connects.
    /// </summary>
    public static WebApplicationBuilder AddLiveEvents(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<EventsOptions>()
            .Bind(builder.Configuration.GetSection(EventsOptions.Section))
            .Validate(o => o.Error() is null, "Invalid Events options: check Events:MaxPerSecondPerKey (1-1000), SubscriberCapacity (16-100000), ReplayCapacity (0-100000), HeartbeatSeconds (1-300) and MaxValueLength (16-1000000).")
            .ValidateOnStart();

        builder.Services.AddSingleton<EventBus>();
        builder.Services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());
        builder.Services.AddSingleton<LatestValues>();
        builder.Services.AddSingleton<IBrokerSession>(sp => sp.GetRequiredService<MqttClientService>());
        builder.Services.AddSingleton<LiveSnapshotService>();
        builder.Services.AddTransient<LiveView>();
        builder.Services.AddHostedService<LiveEventBridge>();

        builder.Services.AddSignalR().AddJsonProtocol(o =>
        {
            o.PayloadSerializerOptions.PropertyNamingPolicy = JsonOptions.Default.PropertyNamingPolicy;
            o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonOptions.Default.PropertyNamingPolicy));
        });
        builder.Services.AddHostedService<LiveHubBroadcaster>();
        return builder;
    }

    public static WebApplication MapLiveEvents(this WebApplication app)
    {
        app.MapEventStream();
        app.MapHub<LiveHub>(LiveHub.Path);
        return app;
    }
}
