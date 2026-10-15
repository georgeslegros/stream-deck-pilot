using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;

namespace StreamDeckPilot.Api.Endpoints;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/devices", async (CatalogueStore catalogue, IDeviceStateProvider stateProvider) =>
        {
            var cat = await catalogue.LoadAsync();
            var result = cat.Devices.Select(d => new
            {
                d.Serial,
                d.Model,
                d.KeyRows,
                d.KeyColumns,
                d.FirstSeen,
                d.LastSeen,
                ConnectionState = stateProvider.GetState(d.Serial).ToString(),
            });
            return Results.Ok(result);
        });

        app.MapGet("/devices/{serial}/status", async (string serial,
            CatalogueStore catalogue, IDeviceStateProvider stateProvider) =>
        {
            var cat = await catalogue.LoadAsync();
            var entry = cat.Devices.FirstOrDefault(d => d.Serial == serial);
            if (entry is null) return ApiProblem.NotFound($"Device '{serial}' is not in the catalogue.");

            return Results.Ok(new
            {
                Serial = serial,
                ConnectionState = stateProvider.GetState(serial).ToString(),
            });
        });

        app.MapPost("/devices/{serial}/force-render", async (string serial,
            CatalogueStore catalogue, DeviceSupervisorService supervisor, DeviceConfigGate gate) =>
        {
            var cat = await catalogue.LoadAsync();
            if (cat.Devices.All(d => d.Serial != serial))
                return ApiProblem.NotFound($"Device '{serial}' is not in the catalogue.");

            var board = supervisor.GetBoard(serial);
            if (board is null || !board.IsConnected)
                return ApiProblem.Conflict($"Device '{serial}' is not connected; it renders from desired state when it reconnects.");

            // Rebuild desired state from the persisted config and fully clear-and-redraw the current
            // page (no page reset). This is the same projection rebuild a config PUT performs, so a
            // force-render reflects the latest config rather than just repainting stale state; the
            // gate keeps it from interleaving with a PUT for the same device.
            using (await gate.EnterAsync(serial))
                await supervisor.ApplyConfigChangeAsync(serial, resetActivePage: false);
            return Results.Accepted();
        });

        // Troubleshooting: inspect which page the device is currently showing and
        // which pages are available as navigation targets.
        app.MapGet("/devices/{serial}/active-page", async (string serial,
            DeviceSupervisorService supervisor, ConfigStore configStore) =>
        {
            var config = await configStore.LoadAsync(serial);
            if (config is null) return ApiProblem.NotFound($"No config for device '{serial}'.");

            var board = supervisor.GetBoard(serial);
            return Results.Ok(new
            {
                Serial = serial,
                ActivePageId = supervisor.GetActivePage(serial),
                Connected = board?.IsConnected == true,
                AvailablePages = config.Pages.Select(p => p.PageId).ToArray(),
            });
        });

        // Troubleshooting: force navigation to a page from the API without having
        // to publish an MQTT NavigateAction. Drives the same SetActivePage path,
        // which re-renders the whole board (clearing keys not bound on the target).
        app.MapPost("/devices/{serial}/navigate", async (string serial,
            NavigateRequest request, DeviceSupervisorService supervisor, ConfigStore configStore) =>
        {
            if (string.IsNullOrWhiteSpace(request.PageId))
                return ApiProblem.BadRequest("pageId is required.");

            var config = await configStore.LoadAsync(serial);
            if (config is null) return ApiProblem.NotFound($"No config for device '{serial}'.");

            var pages = config.Pages.Select(p => p.PageId).ToArray();
            if (!pages.Contains(request.PageId))
                return ApiProblem.BadRequest(
                    $"Page '{request.PageId}' does not exist for device '{serial}'.",
                    new Dictionary<string, object?> { ["availablePages"] = pages });

            supervisor.SetActivePage(serial, request.PageId);

            var board = supervisor.GetBoard(serial);
            return Results.Ok(new
            {
                Serial = serial,
                ActivePageId = request.PageId,
                // Whether the redraw actually reached hardware. If false, the page
                // is set and will render on the next connect from desired state.
                Rendered = board?.IsConnected == true,
            });
        });
    }
}

public sealed record NavigateRequest(string PageId);
