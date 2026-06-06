using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Supervision;

namespace StreamDeckPilot.Infrastructure.Staleness;

public sealed class StalenessMonitor(
    ConfigStore configStore,
    LastUpdatedStore lastUpdated,
    DesiredStateStore desiredState,
    IDeviceRenderer renderer,
    DeviceSupervisorService supervisor,
    ILogger<StalenessMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TickAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync()
    {
        var serials = await configStore.ListSerialsAsync();
        var now = DateTime.UtcNow;

        foreach (var serial in serials)
        {
            var config = await configStore.LoadAsync(serial);
            if (config is null) continue;

            foreach (var page in config.Pages)
            {
                if (page is not ButtonGridPage grid) continue;
                foreach (var button in grid.Buttons)
                {
                    var timeout = button.Inbound?.StalenessTimeout;
                    if (timeout is null) continue;

                    var last = lastUpdated.GetLastUpdated(serial, page.PageId, button.KeyIndex);
                    if (last is null) continue; // Never received a value — placeholder handles this

                    var stale = now - last.Value > timeout.Value;
                    var current = desiredState.Get(serial, page.PageId, button.KeyIndex);
                    if (current is null) continue;

                    if (stale && !current.IsDimmed)
                    {
                        var dimmed = current with { IsDimmed = true };
                        desiredState.Set(serial, page.PageId, button.KeyIndex, dimmed);
                        var board = supervisor.GetBoard(serial);
                        if (board?.IsConnected == true)
                            renderer.RenderButton(board, serial, button.KeyIndex, dimmed);

                        logger.LogInformation(
                            "Button {ButtonId} on {Serial} marked stale (no update for {Elapsed:0.0}s)",
                            button.ButtonId, serial, (now - last.Value).TotalSeconds);
                    }
                    else if (!stale && current.IsDimmed)
                    {
                        // Value arrived recently — un-dim (MQTT pipeline should have already done this,
                        // but handle the edge case here too)
                        var undimmed = current with { IsDimmed = false };
                        desiredState.Set(serial, page.PageId, button.KeyIndex, undimmed);
                        var board = supervisor.GetBoard(serial);
                        if (board?.IsConnected == true)
                            renderer.RenderButton(board, serial, button.KeyIndex, undimmed);
                    }
                }
            }
        }
    }
}
