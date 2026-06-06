using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using StreamDeckPilot.Api.Endpoints;
using StreamDeckPilot.Api.Middleware;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Observability;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Staleness;
using StreamDeckPilot.Infrastructure.StreamDeck;
using StreamDeckPilot.Infrastructure.Supervision;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .Enrich.FromLogContext()
       .WriteTo.Console(new CompactJsonFormatter()));

// Storage & persistence
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
// MigrationRunner with empty list at v1 — add IMigration implementations here when schema changes
builder.Services.AddSingleton<MigrationRunner>(_ => new MigrationRunner([]));
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
builder.Services.AddSingleton<IDeviceRenderer>(sp =>
    new DeviceRenderer(
        sp.GetRequiredService<KeyBitmapComposer>(),
        sp.GetRequiredService<StreamDeckMetrics>()));

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
        metrics: sp.GetRequiredService<StreamDeckMetrics>()));
builder.Services.AddSingleton<IDeviceStateProvider>(sp => sp.GetRequiredService<DeviceSupervisorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceSupervisorService>());

// MQTT
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddSingleton<ButtonTopicIndex>();
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
        sp.GetRequiredService<StreamDeckMetrics>()));
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

builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var converter in JsonOptions.Default.Converters)
        o.SerializerOptions.Converters.Add(converter);
    o.SerializerOptions.PropertyNamingPolicy = JsonOptions.Default.PropertyNamingPolicy;
    o.SerializerOptions.WriteIndented = JsonOptions.Default.WriteIndented;
});

var app = builder.Build();

// Wire observable gauges using lazy lambdas — avoids circular DI dependency
app.Services.GetRequiredService<StreamDeckMetrics>().RegisterObservableGauges(
    () => app.Services.GetRequiredService<DeviceSupervisorService>().GetAllStates(),
    () => app.Services.GetRequiredService<MqttClientService>().IsConnected);

if (string.IsNullOrWhiteSpace(app.Configuration["ApiKey"]))
    app.Logger.LogWarning("ApiKey is not configured — all requests will be rejected with 401. Set the ApiKey environment variable.");

app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapOpenApi("/openapi.json");
app.MapDeviceEndpoints();
app.MapConfigEndpoints();
app.MapImageEndpoints();

app.Run();

public partial class Program { }
