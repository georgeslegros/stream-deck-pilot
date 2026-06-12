using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Validation;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Supervision;
using System.Text.Json;
using System.Text.Json.Nodes;
using static StreamDeckPilot.Core.SchemaVersions;

namespace StreamDeckPilot.Api.Endpoints;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/devices/{serial}/config", async (string serial, ConfigStore configStore) =>
        {
            var config = await configStore.LoadAsync(serial);
            return config is null ? Results.NotFound() : Results.Ok(config);
        });

        app.MapPut("/devices/{serial}/config", async (string serial,
            HttpRequest request, CatalogueStore catalogue, ConfigStore configStore,
            IConfigChangeNotifier notifier, DeviceSupervisorService supervisor) =>
        {
            var cat = await catalogue.LoadAsync();
            var device = cat.Devices.FirstOrDefault(d => d.Serial == serial);
            if (device is null)
                return Results.BadRequest(new { errors = new[] { $"Device '{serial}' is not in the catalogue. Plug it in first." } });

            DeviceConfig config;
            try
            {
                config = await JsonSerializer.DeserializeAsync<DeviceConfig>(
                    request.Body, JsonOptions.Default)
                    ?? throw new JsonException("Null body");
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { errors = new[] { $"Invalid JSON: {ex.Message}" } });
            }

            if (config.Serial != serial)
                return Results.BadRequest(new { errors = new[] { $"Config serial '{config.Serial}' does not match URL serial '{serial}'." } });

            var validation = ConfigValidator.ValidateConfig(config, device);
            if (!validation.IsValid)
                return Results.BadRequest(new { errors = validation.Errors });

            await configStore.SaveAsync(config);
            await notifier.NotifyConfigChangedAsync(serial);

            // Rebuild the live projection: a full clear-and-redraw always happens (removed/moved
            // buttons leave no ghost); the page reset is opt-out via ?resetPage=false so frequent
            // partial updates can keep the user on their current page.
            var resetPage = true;
            if (request.Query.TryGetValue("resetPage", out var rp) && bool.TryParse(rp, out var parsed))
                resetPage = parsed;
            await supervisor.ApplyConfigChangeAsync(serial, resetActivePage: resetPage);

            return Results.NoContent();
        });

        app.MapPost("/config/upgrade", async (HttpRequest request, MigrationRunner migration) =>
        {
            JsonObject doc;
            try
            {
                var node = await JsonNode.ParseAsync(request.Body)
                           ?? throw new JsonException("Empty body");
                doc = node.AsObject();
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
            }

            try
            {
                var migrated = migration.Migrate(doc, ConfigMinimumSupported, ConfigCurrentVersion);
                return Results.Ok(migrated);
            }
            catch (UnsupportedSchemaVersionException ex)
            {
                return Results.UnprocessableEntity(new
                {
                    error = "unsupported_schema_version",
                    message = ex.Message
                });
            }
        });
    }
}
