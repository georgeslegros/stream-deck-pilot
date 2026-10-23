using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Validation;
using StreamDeckPilot.Infrastructure.Events;
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
            return config is null
                ? ApiProblem.NotFound($"No config for device '{serial}'.")
                : Results.Ok(config);
        });

        app.MapPut("/devices/{serial}/config", async (string serial,
            HttpRequest request, CatalogueStore catalogue, ConfigStore configStore,
            IConfigChangeNotifier notifier, DeviceSupervisorService supervisor, DeviceConfigGate gate, IEventBus events) =>
        {
            var cat = await catalogue.LoadAsync();
            var device = cat.Devices.FirstOrDefault(d => d.Serial == serial);
            if (device is null)
                return ApiProblem.Invalid([$"Device '{serial}' is not in the catalogue. Plug it in first."]);

            DeviceConfig config;
            try
            {
                config = await JsonSerializer.DeserializeAsync<DeviceConfig>(
                    request.Body, JsonOptions.Default)
                    ?? throw new JsonException("Null body");
            }
            catch (JsonException ex)
            {
                return Rejected([$"Invalid JSON: {ex.Message}"]);
            }
            catch (NotSupportedException ex)
            {
                // A page or action without its type discriminator (pageType / type) can't be
                // deserialised polymorphically; System.Text.Json reports that as NotSupported (#78).
                return Rejected([$"Invalid JSON: {ex.Message}"]);
            }

            if (config.Serial != serial)
                return Rejected([$"Config serial '{config.Serial}' does not match URL serial '{serial}'."]);

            var validation = ConfigValidator.ValidateConfig(config, device);
            if (!validation.IsValid)
                return Rejected(validation.Errors);

            var resetPage = true;
            if (request.Query.TryGetValue("resetPage", out var rp) && bool.TryParse(rp, out var parsed))
                resetPage = parsed;

            // One save-and-apply at a time per device, so the live projection always matches the
            // config that was saved last (#79).
            using (await gate.EnterAsync(serial))
            {
                await configStore.SaveAsync(config);
                await notifier.NotifyConfigChangedAsync(serial);

                // Rebuild the live projection: a full clear-and-redraw always happens (removed/moved
                // buttons leave no ghost); the page reset is opt-out via ?resetPage=false so frequent
                // partial updates can keep the user on their current page.
                await supervisor.ApplyConfigChangeAsync(serial, resetActivePage: resetPage);
                await notifier.RefreshRetainedAsync(serial);
            }

            events.Publish(new ConfigChanged { Serial = serial });
            return Results.NoContent();

            // A refused upload of a catalogued deck's config is a live event too (#108).
            IResult Rejected(IReadOnlyList<string> errors)
            {
                events.Publish(new ConfigRejected(errors) { Serial = serial });
                return ApiProblem.Invalid(errors);
            }
        });

        app.MapPost("/config/upgrade", async (HttpRequest request, MigrationRunner migration) =>
        {
            JsonObject doc;
            try
            {
                var node = await JsonNode.ParseAsync(request.Body)
                           ?? throw new JsonException("Empty body");
                if (node is not JsonObject obj)
                    return ApiProblem.BadRequest("The body must be a JSON object (a device config document).");
                doc = obj;
            }
            catch (JsonException ex)
            {
                return ApiProblem.BadRequest($"Invalid JSON: {ex.Message}");
            }

            try
            {
                var migrated = migration.Migrate(doc, ConfigMinimumSupported, ConfigCurrentVersion);
                return Results.Ok(migrated);
            }
            catch (InvalidSchemaVersionException ex)
            {
                return ApiProblem.BadRequest(ex.Message);
            }
            catch (UnsupportedSchemaVersionException ex)
            {
                return ApiProblem.Unprocessable("unsupported_schema_version", ex.Message);
            }
        });
    }
}
