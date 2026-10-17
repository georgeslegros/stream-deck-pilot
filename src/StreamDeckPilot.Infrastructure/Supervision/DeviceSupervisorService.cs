using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenMacroBoard.SDK;
using StreamDeckPilot.Infrastructure.Brightness;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Core.DeviceState;
using StreamDeckPilot.Core.Models.Catalogue;
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
    private readonly BrightnessController _brightness;
    private readonly BrokerIndicator? _brokerIndicator;
    private StreamDeckMetrics? _metrics;

    private readonly ConcurrentDictionary<string, DeviceConnectionState> _states = new();
    private readonly ConcurrentDictionary<string, IMacroBoard> _boards = new();
    private readonly ConcurrentDictionary<string, byte> _openedPaths = new();
    private readonly ConcurrentDictionary<string, string> _boardPaths = new(); // serial → hid path
    // Devices whose catalogue entry could not be written yet; retried on every scan (#68).
    private readonly ConcurrentDictionary<string, DeviceEntry> _pendingCatalogue = new();

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
        StreamDeckMetrics? metrics = null,
        BrightnessController? brightness = null,
        BrokerIndicator? brokerIndicator = null,
        BlinkScheduler? blink = null)
    {
        _library = library;
        _catalogue = catalogue;
        _configStore = configStore;
        _desiredState = desiredState;
        _activePages = activePages;
        _renderer = renderer;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _metrics = metrics;
        _brightness = brightness ?? new BrightnessController(Options.Create(new BrightnessOptions()));
        _brightness.EffectiveChanged += ApplyBrightnessToAll;
        if (brokerIndicator is not null)
            brokerIndicator.Changed += _ => RepaintActivePages();
        _brokerIndicator = brokerIndicator;
        if (blink is not null)
            blink.Toggled += RepaintBlinkingKeys;
    }

    // One blink half-period elapsed (#23): repaint only the keys that blink, on the page on screen.
    // Nothing is painted while the deck is dark (brightness 0, not woken by a press) or for keys the
    // broker-offline look has taken over, so a blink never turns into a stream of identical writes.
    private void RepaintBlinkingKeys(string serial)
    {
        if (!_boards.TryGetValue(serial, out var board) || !board.IsConnected) return;
        if (_brightness.Effective == 0) return;
        var pageId = _activePages.GetActivePage(serial);
        if (pageId is null) return;

        var offline = _brokerIndicator?.ShowOffline == true;
        foreach (var (keyIndex, state) in _desiredState.GetPage(serial, pageId))
        {
            if (!state.BlinkActive || (offline && state.DependsOnBroker)) continue;
            try { _renderer.RenderLatest(board, serial, pageId, keyIndex, _desiredState); }
            catch (Exception ex) { _logger.LogWarning(ex, "Blink repaint failed for {Serial} key {Key}", serial, keyIndex); }
        }
    }

    // The broker-offline overlay is applied at paint time, so turning it on or off is a repaint of
    // what each deck shows now, from desired state (#26). Retained values that arrive after a
    // reconnect paint themselves through the MQTT pipeline as usual.
    private void RepaintActivePages()
    {
        foreach (var (serial, board) in _boards)
        {
            var pageId = _activePages.GetActivePage(serial);
            if (pageId is null || !board.IsConnected) continue;
            try { _renderer.RenderAll(board, serial, pageId, _desiredState); }
            catch (Exception ex) { _logger.LogWarning(ex, "Repaint after a broker state change failed for {Serial}", serial); }
        }
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

        // Scan forever, not just at startup: after a host boot the container can start before the
        // kernel creates the hidraw node, and an unplugged deck is only re-opened by a fresh scan.
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            do
            {
                try { await ScanAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Stream Deck scan failed — retrying in {Interval}", _pollInterval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Stops scanning, then releases every board. Disposing a StreamDeckSharp board shows the
    /// Elgato logo, so a stopped service doesn't leave the deck showing values that no longer update
    /// (#69). Idempotent: a board is removed before it is disposed, so a second stop finds nothing.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        foreach (var (serial, board) in _boards)
        {
            if (!_boards.TryRemove(new KeyValuePair<string, IMacroBoard>(serial, board))) continue;
            if (_boardPaths.TryRemove(serial, out var path))
                _openedPaths.TryRemove(path, out _);

            _states[serial] = DeviceConnectionState.Disconnected;
            _logger.LogInformation("Device {Serial} released on shutdown", serial);
            // The host stops services in reverse registration order, so the MQTT and staleness
            // services (registered after this one) have already stopped; DisposeQuietly swallows
            // anything a straggling write throws.
            DisposeQuietly(board);
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        ReleaseDisconnectedBoards();
        await RetryPendingCatalogueAsync();

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
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stream Deck at {Path} not openable yet — will retry", deviceRef.Path);
                continue;
            }

            // StreamDeckSharp returns a disconnected board instead of throwing when the hidraw node
            // is not usable yet, and a freshly enumerated deck can report an empty serial. Neither
            // may be registered: drop it and let the next scan try again.
            var serial = board.IsConnected ? TryReadSerial(board) : null;
            if (string.IsNullOrEmpty(serial))
            {
                _logger.LogWarning("Stream Deck at {Path} not ready (no connection or serial) — will retry", deviceRef.Path);
                DisposeQuietly(board);
                continue;
            }

            if (!_boards.TryAdd(serial, board))
            {
                DisposeQuietly(board);
                continue;
            }

            _openedPaths.TryAdd(deviceRef.Path, 0);
            _boardPaths[serial] = deviceRef.Path;
            _logger.LogInformation("Device {Serial} discovered at {Path}", serial, deviceRef.Path);

            // A catalogue failure (corrupt file, full or read-only volume) must not leave the board
            // registered but unwired: the deck still works, and the entry is retried on later scans.
            await TryRecordInCatalogueAsync(new(
                serial, deviceRef.DeviceName,
                board.Keys.CountY, board.Keys.CountX,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            // Events from a board that has since been released (replaced after a replug) are ignored.
            board.ConnectionStateChanged += (_, e) =>
            {
                if (!IsCurrentBoard(serial, board)) return;
                _ = Task.Run(() => HandleConnectionChangeAsync(serial, board, e.NewConnectionState));
            };

            board.KeyStateChanged += (_, e) =>
            {
                if (!e.IsDown || !IsCurrentBoard(serial, board)) return;
                // A press on a dark deck only wakes it; its gesture must not run, nor is it counted.
                if (_brightness.OnKeyPressed())
                {
                    _logger.LogInformation("Key {Key} on {Serial} pressed while dark — woke deck, press swallowed", e.Key, serial);
                    return;
                }
                _metrics?.ButtonPresses.Add(1, [new("serial", serial), new("key", e.Key)]);
                ButtonPressed?.Invoke(serial, e.Key);
            };

            await HandleConnectionChangeAsync(serial, board, connected: true);
        }
    }

    private async Task TryRecordInCatalogueAsync(DeviceEntry entry)
    {
        try
        {
            await _catalogue.AppendDeviceAsync(entry);
            if (_pendingCatalogue.TryRemove(entry.Serial, out _))
                _logger.LogInformation("Device {Serial} recorded in the catalogue after an earlier failure", entry.Serial);
        }
        catch (Exception ex)
        {
            // Logged once per device until it succeeds, not on every scan.
            if (_pendingCatalogue.TryAdd(entry.Serial, entry))
                _logger.LogError(ex, "Failed to record device {Serial} in the catalogue — the deck stays usable; retrying on each scan", entry.Serial);
            else
                _pendingCatalogue[entry.Serial] = entry;
        }
    }

    private async Task RetryPendingCatalogueAsync()
    {
        foreach (var entry in _pendingCatalogue.Values)
            await TryRecordInCatalogueAsync(entry with { LastSeen = DateTimeOffset.UtcNow });
    }

    // A disconnected board is never reopened by StreamDeckSharp inside a container: it relies on udev
    // monitor events, which do not reach the container. Release it so the next enumeration opens the
    // device afresh — whether it comes back on the same hidraw node or a new one.
    private void ReleaseDisconnectedBoards()
    {
        foreach (var (serial, board) in _boards)
        {
            if (board.IsConnected) continue;
            if (!_boards.TryRemove(new KeyValuePair<string, IMacroBoard>(serial, board))) continue;

            if (_boardPaths.TryRemove(serial, out var path))
                _openedPaths.TryRemove(path, out _);

            _states[serial] = DeviceConnectionState.Disconnected;
            _logger.LogInformation("Device {Serial} released — waiting for it to reappear", serial);
            DisposeQuietly(board);
        }
    }

    // Reads the current level rather than trusting the event argument, so racing changes (MQTT set
    // vs. wake timeout) always converge on the latest value.
    private void ApplyBrightnessToAll(int _)
    {
        var level = _brightness.Effective;
        foreach (var (serial, board) in _boards)
        {
            if (!board.IsConnected) continue;
            try { board.SetBrightness((byte)level); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to set brightness on {Serial}", serial); }
        }
    }

    private bool IsCurrentBoard(string serial, IMacroBoard board) =>
        _boards.TryGetValue(serial, out var current) && ReferenceEquals(current, board);

    private string? TryReadSerial(IMacroBoard board)
    {
        try
        {
            var raw = board.GetSerialNumber();
            return raw is null ? null : new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get serial number from device");
            return null;
        }
    }

    private void DisposeQuietly(IMacroBoard board)
    {
        try { board.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Ignoring error while disposing a Stream Deck handle"); }
    }

    private async Task HandleConnectionChangeAsync(string serial, IMacroBoard board, bool connected)
    {
        // Re-checked here as the handler runs on the thread pool: a released board's late event
        // must not overwrite the state of the board that replaced it.
        if (!IsCurrentBoard(serial, board)) return;

        if (connected)
        {
            var isReconnect = _states.TryGetValue(serial, out var prev) && prev == DeviceConnectionState.Disconnected;
            _states[serial] = DeviceConnectionState.Connected;
            if (isReconnect) _metrics?.DeviceReconnects.Add(1, [new("serial", serial)]);
            _logger.LogInformation("Device {Serial} connected — rendering from desired state", serial);
            try
            {
                board.SetBrightness((byte)_brightness.Effective);
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

        SeedDesiredState(serial, config);
        _renderer.RenderAll(board, serial, pageId, _desiredState);
    }

    // Seed placeholder state for every button that has no real value yet. On connect this preserves
    // any live MQTT value already present (the dimmed/empty check); after a Clear (config change) it
    // seeds every button. Text zones fall back to their static labels until live data resolves them.
    private void SeedDesiredState(string serial, DeviceConfig config)
    {
        foreach (var page in config.Pages)
        {
            if (page is not ButtonGridPage grid) continue;
            foreach (var button in grid.Buttons)
            {
                var existing = _desiredState.Get(serial, page.PageId, button.KeyIndex);
                if (existing is { IsDimmed: false }) continue;

                var placeholder = button.Inbound?.ExpectsRetained == true;
                _desiredState.Set(serial, page.PageId, button.KeyIndex, new ButtonRenderState(
                    button.ButtonId,
                    null,
                    placeholder ? "builtin:placeholder" : button.Display.BaseIcon,
                    button.Display.IconPlacement,
                    button.Display.Center?.Label,
                    button.Display.Bottom?.Label,
                    IsDimmed: placeholder,
                    DependsOnBroker: ButtonRenderState.ButtonDependsOnBroker(button)));
            }
        }
    }

    /// <summary>
    /// Re-applies the persisted config to the live projection without a restart or reconnect:
    /// rebuilds desired state from the new config (so removed/moved buttons leave no ghost),
    /// optionally resets to the first page, and fully repaints the board — the same full
    /// clear-and-redraw that happens on connect. A full key repaint always happens; only the
    /// page reset is gated by <paramref name="resetActivePage"/>. Safe when the device is offline
    /// (state is updated and paints on next connect).
    /// </summary>
    public async Task ApplyConfigChangeAsync(string serial, bool resetActivePage)
    {
        var board = _boards.TryGetValue(serial, out var b) ? b : null;
        var config = await _configStore.LoadAsync(serial);

        // Rebuild from scratch so a key removed/moved in the new layout leaves no stale image.
        _desiredState.Clear(serial);

        if (config is null || config.Pages.Count == 0)
        {
            _activePages.Clear(serial);
            if (board?.IsConnected == true)
                _renderer.RenderAll(board, serial, string.Empty, _desiredState); // blanks every key
            return;
        }

        // Reset to the first page on request; otherwise keep the current page — but fall back to
        // the first page if the current one no longer exists in the new config.
        var pageIds = config.Pages.Select(p => p.PageId).ToHashSet();
        var pageId = _activePages.GetActivePage(serial);
        if (resetActivePage || pageId is null || !pageIds.Contains(pageId))
            pageId = config.Pages[0].PageId;
        _activePages.SetActivePage(serial, pageId);

        SeedDesiredState(serial, config);

        if (board?.IsConnected == true)
            _renderer.RenderAll(board, serial, pageId, _desiredState);
    }
}
