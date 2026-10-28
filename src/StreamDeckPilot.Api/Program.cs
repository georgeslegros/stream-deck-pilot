using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using StreamDeckPilot.Api.Endpoints;
using StreamDeckPilot.Api.Events;
using StreamDeckPilot.Api.Middleware;
using StreamDeckPilot.Api.Proxy;
using StreamDeckPilot.Api.Security;
using StreamDeckPilot.Core.Security;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Controls;
using StreamDeckPilot.Infrastructure.Gestures;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Navigation;
using StreamDeckPilot.Infrastructure.Observability;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Sparkline;
using StreamDeckPilot.Infrastructure.Staleness;
using StreamDeckPilot.Infrastructure.StreamDeck;
using StreamDeckPilot.Infrastructure.Supervision;
using StreamDeckPilot.Web;
using StreamDeckPilot.Web.Auth;

var builder = WebApplication.CreateBuilder(args);
builder.AddReverseProxySupport();

builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .Enrich.FromLogContext()
       .WriteTo.Console(new CompactJsonFormatter()));

// Storage & persistence
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
// MigrationRunner — register IMigration implementations here as the schema evolves.
// (Catalogue is still v1 so these only ever apply to config documents.)
builder.Services.AddSingleton<MigrationRunner>(_ => new MigrationRunner([
    new ConfigV1ToV2Migration(),
]));
builder.Services.AddSingleton<CatalogueStore>(sp =>
    new CatalogueStore(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>(),
        sp.GetRequiredService<MigrationRunner>()));
builder.Services.AddSingleton<ConfigStore>(sp =>
    new ConfigStore(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>(),
        sp.GetRequiredService<MigrationRunner>()));

// Rendering
builder.Services.AddSingleton<DesiredStateStore>();
builder.Services.AddSingleton<ActivePageStore>();
builder.Services.AddSingleton<CustomImageSource>();
builder.Services.AddSingleton<IconResolver>();
builder.Services.AddSingleton<KeyBitmapComposer>();
builder.Services.AddSingleton<LastUpdatedStore>();

// Observability — StreamDeckMetrics owns the Meter; gauges are registered after DI is built
builder.Services.AddSingleton<StreamDeckMetrics>();
// Broker-offline overlay (#26): fed by the MQTT service, read by the renderer, repainted by the supervisor
builder.Services.AddSingleton<BrokerIndicator>(sp =>
    new BrokerIndicator(
        TimeSpan.FromSeconds(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MqttOptions>>().Value.OfflineIndicatorDelaySeconds),
        logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BrokerIndicator>>()));
// Press feedback (#22): set by the MQTT service on a press, read by the renderer, repainted by the MQTT service
builder.Services.Configure<PressFeedbackOptions>(builder.Configuration.GetSection("PressFeedback"));
builder.Services.AddSingleton<PressFeedbackTracker>(sp =>
    new PressFeedbackTracker(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PressFeedbackOptions>>().Value,
        broker: sp.GetRequiredService<BrokerIndicator>(),
        logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PressFeedbackTracker>>()));
// Blinking keys (#23, #131): the scheduler times the frames, the renderer applies them, the supervisor
// repaints. Strengths and periods out of range are clamped; an unknown style is a startup error.
builder.Services.AddOptions<BlinkOptions>()
    .Bind(builder.Configuration.GetSection("Blink"))
    .Validate(o => BlinkSpec.Styles.Contains(o.Style, StringComparer.OrdinalIgnoreCase),
        $"Blink:Style must be one of: {string.Join(", ", BlinkSpec.Styles)}.")
    .ValidateOnStart();
builder.Services.AddSingleton<BlinkScheduler>(sp =>
    new BlinkScheduler(
        sp.GetRequiredService<DesiredStateStore>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BlinkOptions>>(),
        logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BlinkScheduler>>()));
// The renderer records each frame it sends in PaintedFrameStore, which the live preview reads (#27).
builder.Services.AddSingleton<PaintedFrameStore>();
builder.Services.AddSingleton<DeviceRenderer>(sp =>
    new DeviceRenderer(
        sp.GetRequiredService<KeyBitmapComposer>(),
        sp.GetRequiredService<StreamDeckMetrics>(),
        sp.GetRequiredService<BrokerIndicator>(),
        sp.GetRequiredService<BlinkScheduler>(),
        sp.GetRequiredService<PressFeedbackTracker>(),
        sp.GetRequiredService<PaintedFrameStore>()));
builder.Services.AddSingleton<IDeviceRenderer>(sp => sp.GetRequiredService<DeviceRenderer>());

// Brightness — desired level set over MQTT, applied by the supervisor, state published by MQTT
builder.Services.Configure<BrightnessOptions>(builder.Configuration.GetSection("Brightness"));
builder.Services.AddSingleton<BrightnessController>(sp =>
    new BrightnessController(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BrightnessOptions>>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BrightnessController>>()));

// Return to the home page after inactivity (off unless Navigation:HomeAfterInactivitySeconds > 0)
builder.Services.Configure<NavigationOptions>(builder.Configuration.GetSection("Navigation"));
builder.Services.AddSingleton<InactivityMonitor>(sp =>
    new InactivityMonitor(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NavigationOptions>>(),
        logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InactivityMonitor>>()));

// Setpoint controls (#132): fed by the MQTT service, seeded by the supervisor, painted from desired state
builder.Services.AddOptions<SetpointOptions>()
    .Bind(builder.Configuration.GetSection("Setpoint"))
    .Validate(o => o.Error() is null, "Invalid Setpoint options: check Setpoint:PendingColour (#RRGGBB), DefaultCommitDelayMs (0-10000), DefaultConfirmTimeoutSeconds (1-600) and FailedFlashMilliseconds (0-10000).")
    .ValidateOnStart();
builder.Services.AddSingleton<SetpointController>(sp =>
    new SetpointController(
        sp.GetRequiredService<DesiredStateStore>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SetpointOptions>>(),
        broker: sp.GetRequiredService<BrokerIndicator>(),
        logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SetpointController>>()));

// Live events (#108): bus, SSE and hub. Before the supervisor's hosted service, so its bridge
// hears the first connection.
builder.AddLiveEvents();

// Device supervision
builder.Services.AddSingleton<IStreamDeckLibrary, StreamDeckLibrary>();
builder.Services.AddSingleton<DeviceSupervisorService>(sp =>
    new DeviceSupervisorService(
        sp.GetRequiredService<IStreamDeckLibrary>(),
        sp.GetRequiredService<CatalogueStore>(),
        sp.GetRequiredService<ConfigStore>(),
        sp.GetRequiredService<DesiredStateStore>(),
        sp.GetRequiredService<ActivePageStore>(),
        sp.GetRequiredService<IDeviceRenderer>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DeviceSupervisorService>>(),
        metrics: sp.GetRequiredService<StreamDeckMetrics>(),
        brightness: sp.GetRequiredService<BrightnessController>(),
        brokerIndicator: sp.GetRequiredService<BrokerIndicator>(),
        blink: sp.GetRequiredService<BlinkScheduler>(),
        inactivity: sp.GetRequiredService<InactivityMonitor>(),
        setpoints: sp.GetRequiredService<SetpointController>()));
builder.Services.AddSingleton<IDeviceStateProvider>(sp => sp.GetRequiredService<DeviceSupervisorService>());
builder.Services.AddSingleton<DevicePreviewService>();
// Registration order matters: the host stops hosted services in reverse order, so the supervisor
// must be registered BEFORE every service that renders to the boards (MQTT, staleness). Those then
// stop first, and the supervisor's StopAsync releases the boards with no renders still arriving
// (#69). Pinned by HostedServiceOrderTests.
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceSupervisorService>());

// MQTT
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddOptions<GestureOptions>()
    .Bind(builder.Configuration.GetSection("Gestures"))
    .Validate(o => o.LongPressMs > 0 && o.DoubleTapMs > 0, "Gestures:LongPressMs and Gestures:DoubleTapMs must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<ButtonTopicIndex>();
builder.Services.Configure<SparklineOptions>(builder.Configuration.GetSection("Sparkline"));
builder.Services.AddSingleton<SparklineHistory>();
builder.Services.AddSingleton<MqttClientService>(sp =>
    new MqttClientService(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MqttOptions>>(),
        sp.GetRequiredService<ConfigStore>(),
        sp.GetRequiredService<DesiredStateStore>(),
        sp.GetRequiredService<ButtonTopicIndex>(),
        sp.GetRequiredService<IDeviceRenderer>(),
        sp.GetRequiredService<DeviceSupervisorService>(),
        sp.GetRequiredService<LastUpdatedStore>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MqttClientService>>(),
        sp.GetRequiredService<StreamDeckMetrics>(),
        sp.GetRequiredService<BrightnessController>(),
        brokerIndicator: sp.GetRequiredService<BrokerIndicator>(),
        pressFeedback: sp.GetRequiredService<PressFeedbackTracker>(),
        gestureOptions: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GestureOptions>>(),
        sparklines: sp.GetRequiredService<SparklineHistory>(),
        setpoints: sp.GetRequiredService<SetpointController>()));
builder.Services.AddSingleton<IConfigChangeNotifier>(sp => sp.GetRequiredService<MqttClientService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttClientService>());
builder.Services.AddHostedService<StalenessMonitor>();

// OpenTelemetry — instruments against the API only; exporter endpoint from OTEL_EXPORTER_OTLP_ENDPOINT env var
builder.Services.AddOpenApi();

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("StreamDeckPilot")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter())
    .WithTracing(t => t
        .AddSource("StreamDeckPilot.Pipeline")
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

// Errors: every failure is an RFC 7807 problem (see ApiProblem). Unhandled exceptions become a
// plain 500 problem without exception details; empty-bodied error statuses get a problem body too.
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<DeviceConfigGate>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var converter in JsonOptions.Default.Converters)
        o.SerializerOptions.Converters.Add(converter);
    o.SerializerOptions.PropertyNamingPolicy = JsonOptions.Default.PropertyNamingPolicy;
    o.SerializerOptions.WriteIndented = JsonOptions.Default.WriteIndented;
});

// API keys: one from configuration today; named, revocable keys (#98) replace this registration.
builder.Services.AddSingleton<IApiKeyValidator, ConfiguredApiKeyValidator>();

// Data Protection keys on the data volume, so sessions and anti-forgery tokens survive restarts (#97).
builder.AddPersistentDataProtection();

// Web UI (#96): off unless Web:Enabled=true; then sign-in is required (#97). See WebUiExtensions.
builder.AddWebUi();

var app = builder.Build();

// Wire observable gauges using lazy lambdas — avoids circular DI dependency
app.Services.GetRequiredService<StreamDeckMetrics>().RegisterObservableGauges(
    () => app.Services.GetRequiredService<DeviceSupervisorService>().GetAllStates(),
    () => app.Services.GetRequiredService<MqttClientService>().IsConnected);

app.LogDataProtectionMode();

if (string.IsNullOrWhiteSpace(app.Configuration["ApiKey"]))
    app.Logger.LogWarning("ApiKey is not configured — all requests will be rejected with 401. Set the ApiKey environment variable.");

// First: forwarded headers, the optional path base and WebSockets (#109, docs/reverse-proxy.md).
app.UseReverseProxySupport();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Web UI sign-in (#97): session cookie, the cross-site WebSocket guard on the circuit and the live
// endpoints, and page authorization. A no-op while the UI is off.
app.UseWebAuth(ApiKeyMiddleware.LiveHubPath, ApiKeyMiddleware.EventStreamPath);
app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapOpenApi(ApiKeyMiddleware.OpenApiPath);
app.MapDeviceEndpoints();
app.MapConfigEndpoints();
app.MapImageEndpoints();
app.MapPreviewEndpoints();
app.MapLiveEvents();
app.MapWebUi();

app.Run();

public partial class Program { }
