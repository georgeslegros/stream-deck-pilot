using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.Sparkline;
using StreamDeckPilot.Infrastructure.Supervision;

namespace StreamDeckPilot.Infrastructure.Staleness;

public sealed class StalenessMonitor(
    ConfigStore configStore,
    LastUpdatedStore lastUpdated,
    DesiredStateStore desiredState,
    IDeviceRenderer renderer,
    DeviceSupervisorService supervisor,
    ILogger<StalenessMonitor> logger,
    SparklineHistory? sparklines = null) : BackgroundService
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

    /// <summary>Test seam: runs between reading a button's state and writing it back.</summary>
    internal Action? AfterStateRead { get; set; }

    internal async Task TickAsync()
    {
        var serials = await configStore.ListSerialsAsync();
        var now = DateTime.UtcNow;

        foreach (var serial in serials)
        {
            var config = await configStore.LoadAsync(serial);
            if (config is null) continue;

            RefreshSparklines(serial, config);

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
                    AfterStateRead?.Invoke();

                    // Every write below is a compare-and-set against the state read above: the MQTT
                    // pipeline writes the same key concurrently, and a plain Set would put the old
                    // value back over a fresh one (#62). If it lost, the fresh state wins; the next
                    // tick re-evaluates it.
                    if (stale && !current.IsDimmed)
                    {
                        var dimmed = current with { IsDimmed = true };
                        if (!desiredState.TryUpdate(serial, page.PageId, button.KeyIndex, current, dimmed)) continue;
                        var board = supervisor.GetBoard(serial);
                        // Only paint the hardware if this button's page is the one on screen.
                        // Desired state is updated above regardless, so it renders correctly on
                        // navigation — without this guard a stale button on an off-screen page
                        // would repaint its key over whatever page is currently displayed.
                        if (board?.IsConnected == true && supervisor.GetActivePage(serial) == page.PageId)
                            renderer.RenderLatest(board, serial, page.PageId, button.KeyIndex, desiredState);

                        logger.LogInformation(
                            "Button {ButtonId} on {Serial} marked stale (no update for {Elapsed:0.0}s)",
                            button.ButtonId, serial, (now - last.Value).TotalSeconds);
                    }
                    else if (!stale && current.IsDimmed)
                    {
                        // Value arrived recently — un-dim (MQTT pipeline should have already done this,
                        // but handle the edge case here too)
                        var undimmed = current with { IsDimmed = false };
                        if (!desiredState.TryUpdate(serial, page.PageId, button.KeyIndex, current, undimmed)) continue;
                        var board = supervisor.GetBoard(serial);
                        // Only paint the hardware if this button's page is the one on screen.
                        // Desired state is updated above regardless, so it renders correctly on
                        // navigation — without this guard a stale button on an off-screen page
                        // would repaint its key over whatever page is currently displayed.
                        if (board?.IsConnected == true && supervisor.GetActivePage(serial) == page.PageId)
                            renderer.RenderLatest(board, serial, page.PageId, button.KeyIndex, desiredState);
                    }
                }
            }
        }
    }

    // Sparkline keys (#25) on the page on screen slide their time window even when no value
    // arrives (on-change topics). SparklineHistory.Refresh rate-limits each key to once per bucket,
    // at least a minute apart, so most ticks repaint nothing; pages with no sparkline cost nothing.
    private void RefreshSparklines(string serial, DeviceConfig config)
    {
        if (sparklines is null) return;
        sparklines.Retain(serial, config.Pages.OfType<ButtonGridPage>()
            .SelectMany(p => p.Buttons).Select(b => b.ButtonId).ToHashSet(StringComparer.Ordinal));

        var activePage = supervisor.GetActivePage(serial);
        if (config.Pages.FirstOrDefault(p => p.PageId == activePage) is not ButtonGridPage grid) return;

        foreach (var button in grid.Buttons)
        {
            if (button.Display?.Sparkline is null) continue;
            var current = desiredState.Get(serial, grid.PageId, button.KeyIndex);
            if (current?.Sparkline is null) continue;   // no value yet: nothing to slide
            if (sparklines.Refresh(serial, button) is not { } series) continue;

            // Compare-and-set like the staleness writes below: a value arriving meanwhile wins.
            if (!desiredState.TryUpdate(serial, grid.PageId, button.KeyIndex, current, current with { Sparkline = series }))
                continue;
            var board = supervisor.GetBoard(serial);
            if (board?.IsConnected == true)
                renderer.RenderLatest(board, serial, grid.PageId, button.KeyIndex, desiredState);
        }
    }
}
