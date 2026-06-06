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
            if (entry is null) return Results.NotFound();

            return Results.Ok(new
            {
                Serial = serial,
                ConnectionState = stateProvider.GetState(serial).ToString(),
            });
        });

        app.MapPost("/devices/{serial}/force-render", (string serial,
            DeviceSupervisorService supervisor, IDeviceRenderer renderer, DesiredStateStore desiredState) =>
        {
            var board = supervisor.GetBoard(serial);
            if (board is null || !board.IsConnected) return Results.Ok(new { message = "Device not connected" });

            var pageId = supervisor.GetActivePage(serial);
            if (pageId is null) return Results.Ok(new { message = "No active page" });

            renderer.RenderAll(board, serial, pageId, desiredState);
            return Results.Accepted();
        });
    }
}
