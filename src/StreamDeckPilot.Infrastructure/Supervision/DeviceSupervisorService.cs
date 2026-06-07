using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenMacroBoard.SDK;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Observability;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Infrastructure.StreamDeck;

namespace StreamDeckPilot.Infrastructure.Supervision;

public sealed class DeviceSupervisorService : BackgroundService, IDeviceStateProvider
{
    private readonly IStreamDeckLibrary _library;
    private readonly CatalogueStore _catalogue;
    private readonly ConfigStore _configStore;
    private readonly DesiredStateStore _desiredState;
    private readonly ActivePageStore _activePages;
    private readonly IDeviceRenderer _renderer;
    private readonly ILogger<DeviceSupervisorService> _logger;
    private readonly TimeSpan _pollInterval;
    private StreamDeckMetrics? _metrics;

    private readonly ConcurrentDictionary<string, DeviceConnectionState> _states = new();
    private readonly ConcurrentDictionary<string, IMacroBoard> _boards = new();
    private readonly ConcurrentDictionary<string, byte> _openedPaths = new();

    // Fired when a key is pressed (IsDown=true); subscribers handle the action
    public event Action<string, int>? ButtonPressed;

    public DeviceSupervisorService(
        IStreamDeckLibrary library,
        CatalogueStore catalogue,
        ConfigStore configStore,
        DesiredStateStore desiredState,
        ActivePageStore activePages,
        IDeviceRenderer renderer,
        ILogger<DeviceSupervisorService> logger,
        TimeSpan? pollInterval = null,
        StreamDeckMetrics? metrics = null)
    {
        _library = library;
        _catalogue = catalogue;
        _configStore = configStore;
        _desiredState = desiredState;
        _activePages = activePages;
        _renderer = renderer;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
        _metrics = metrics;
    }

    public DeviceConnectionState GetState(string serial) =>
        _states.TryGetValue(serial, out var s) ? s : DeviceConnectionState.Unknown;

    public IReadOnlyDictionary<string, DeviceConnectionState> GetAllStates() =>
        new Dictionary<string, DeviceConnectionState>(_states);

    public IMacroBoard? GetBoard(string serial) =>
        _boards.TryGetValue(serial, out var b) ? b : null;

    public string? GetActivePage(string serial) =>
        _activePages.GetActivePage(serial);

    public void SetActivePage(string serial, string pageId)
    {
        _activePages.SetActivePage(serial, pageId);
        if (_boards.TryGetValue(serial, out var board))
            _renderer.RenderAll(board, serial, pageId, _desiredState);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StreamDeckHardwareRegistration.Register();
        await ScanAsync(stoppingToken);

        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ScanAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        IReadOnlyList<IStreamDeckDeviceRef> refs;
        try { refs = _library.Enumerate(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate Stream Deck devices");
            return;
        }

        foreach (var deviceRef in refs)
        {
            if (_openedPaths.ContainsKey(deviceRef.Path)) continue;

            IMacroBoard board;
            try { board = deviceRef.Open(); }
            catch { continue; }

            string serial;
            try
            {
                var raw = board.GetSerialNumber();
                serial = new string(raw.Where(c => !char.IsControl(c)).ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get serial number from device");
                board.Dispose();
                continue;
            }

            if (!_boards.TryAdd(serial, board))
            {
                board.Dispose();
                continue;
            }

            _openedPaths.TryAdd(deviceRef.Path, 0);
            _logger.LogInformation("Device {Serial} discovered", serial);

            await _catalogue.AppendDeviceAsync(new(
                serial, deviceRef.DeviceName,
                board.Keys.CountY, board.Keys.CountX,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            board.ConnectionStateChanged += (_, e) =>
                _ = Task.Run(() => HandleConnectionChangeAsync(serial, board, e.NewConnectionState));

            board.KeyStateChanged += (_, e) =>
            {
                if (!e.IsDown) return;
                ButtonPressed?.Invoke(serial, e.Key);
                _metrics?.ButtonPresses.Add(1, [new("serial", serial), new("key", e.Key)]);
            };

            await HandleConnectionChangeAsync(serial, board, connected: true);
        }
    }

    private async Task HandleConnectionChangeAsync(string serial, IMacroBoard board, bool connected)
    {
        if (connected)
        {
            var wasConnected = _states.TryGetValue(serial, out var prev) && prev == DeviceConnectionState.Connected;
            _states[serial] = DeviceConnectionState.Connected;
            if (wasConnected) _metrics?.DeviceReconnects.Add(1, [new("serial", serial)]);
            _logger.LogInformation("Device {Serial} connected — rendering from desired state", serial);
            try
            {
                board.SetBrightness(80);
                await RenderFromDesiredStateAsync(serial, board);
            }
            catch (Exception ex) { _logger.LogError(ex, "Re-render failed for {Serial}", serial); }
        }
        else
        {
            _states[serial] = DeviceConnectionState.Disconnected;
            _logger.LogWarning("Device {Serial} disconnected", serial);
        }
    }

    private async Task RenderFromDesiredStateAsync(string serial, IMacroBoard board)
    {
        var config = await _configStore.LoadAsync(serial);
        if (config is null || config.Pages.Count == 0) return;

        var pageId = _activePages.GetActivePage(serial);
        if (pageId is null)
        {
            pageId = config.Pages[0].PageId;
            _activePages.SetActivePage(serial, pageId);
        }

        // Seed placeholder state for every button that has no real value yet.
        // Run on every connect/reconnect so buttons added after first connect are not skipped.
        foreach (var page in config.Pages)
        {
            if (page is not ButtonGridPage grid) continue;
            foreach (var button in grid.Buttons)
            {
                // Only initialise to placeholder if MQTT has not already provided a real value.
                var existing = _desiredState.Get(serial, page.PageId, button.KeyIndex);
                if (existing is { IsDimmed: false }) continue;

                var placeholder = button.Inbound?.ExpectsRetained == true;

                // No live data yet → text zones fall back to their static labels (templates
                // have nothing to resolve against, so only a zone's Label can show).
                _desiredState.Set(serial, page.PageId, button.KeyIndex, new ButtonRenderState(
                    button.ButtonId,
                    null,
                    placeholder ? "builtin:placeholder" : button.Display.BaseIcon,
                    button.Display.IconPlacement,
                    button.Display.Center?.Label,
                    button.Display.Bottom?.Label,
                    IsDimmed: placeholder));
            }
        }

        _renderer.RenderAll(board, serial, pageId, _desiredState);
    }
}
