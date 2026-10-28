using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Infrastructure.Sparkline;

/// <summary>
/// Keeps sparkline history (#25) across restarts (#144): one file per device under
/// <c>{Storage:BaseDirectory}/sparklines/{serial}.json</c>, written atomically every
/// <see cref="SparklineOptions.SaveIntervalSeconds"/> when something changed and once more when
/// the host starts stopping (before the supervisor releases the decks). Loaded when the host is
/// starting, before MQTT delivers the first value, so the first render already shows the trend.
/// A missing, corrupt or other-version file is logged and ignored, never fatal.
/// </summary>
public sealed class SparklinePersistence(
    SparklineHistory history,
    ConfigStore configStore,
    IOptions<StorageOptions> storage,
    IOptions<SparklineOptions> options,
    ILogger<SparklinePersistence> logger,
    TimeProvider? timeProvider = null) : BackgroundService, IHostedLifecycleService
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private long _savedVersion = -1;

    private string Directory => Path.Combine(storage.Value.BaseDirectory, "sparklines");

    // ── Lifecycle ────────────────────────────────────────────────────────────

    Task IHostedLifecycleService.StartingAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);
    Task IHostedLifecycleService.StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken) => SaveQuietlyAsync(cancellationToken);
    Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.SaveIntervalSeconds <= 0) return;   // shutdown save only
        var interval = TimeSpan.FromSeconds(options.Value.SaveIntervalSeconds);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, _time, stoppingToken);
                await SaveQuietlyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes every device's history when it changed since the last save; returns whether it
    /// wrote. A file with no history in memory is removed only when the device's config loads and
    /// has no sparkline left: history that is merely dormant (config missing or unreadable, file
    /// not loaded) is kept so it can come back.
    /// </summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            var version = history.Version;
            if (version == _savedVersion) return false;

            var snapshot = history.Export();   // copied under the buffers' locks; no lock held below
            var savedAt = _time.GetUtcNow();
            foreach (var (serial, buttons) in snapshot)
            {
                if (!IsSafeFileName(serial))
                {
                    logger.LogWarning("Not saving the sparkline history of device '{Serial}': unsafe as a file name", serial);
                    continue;
                }
                var file = new SparklineFile(SchemaVersion, savedAt.ToUnixTimeMilliseconds(),
                    buttons.ToDictionary(b => b.Key,
                        b => b.Value.Select(p => new SparklinePointFile(p.At.ToUnixTimeMilliseconds(), p.Value)).ToList(),
                        StringComparer.Ordinal));
                await AtomicFileWriter.WriteJsonAsync(PathFor(serial), file, Json, cancellationToken);
            }

            if (System.IO.Directory.Exists(Directory))
                foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json"))
                {
                    var serial = Path.GetFileNameWithoutExtension(path);
                    if (!snapshot.ContainsKey(serial) && await HasNoSparklinesAsync(serial))
                        File.Delete(path);
                }

            _savedVersion = version;
            return true;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task<bool> HasNoSparklinesAsync(string serial)
    {
        try
        {
            var config = await configStore.LoadAsync(serial);
            return config is not null && !config.Pages.OfType<ButtonGridPage>()
                .SelectMany(p => p.Buttons).Any(b => b.Display?.Sparkline is not null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Keeping the sparkline history of {Serial}: its config could not be read", serial);
            return false;
        }
    }

    private async Task SaveQuietlyAsync(CancellationToken cancellationToken)
    {
        try { await SaveAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Could not save sparkline history to {Directory}", Directory); }
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores the saved history of every device that still has a config, keeping only buttons
    /// that still have a sparkline. Never throws for a bad file.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(Directory)) return;

        var restored = 0;
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            var serial = Path.GetFileNameWithoutExtension(path);
            try
            {
                SparklineFile? file;
                await using (var stream = File.OpenRead(path))
                    file = await JsonSerializer.DeserializeAsync<SparklineFile>(stream, Json, cancellationToken);

                if (file is null || file.SchemaVersion != SchemaVersion || file.Buttons is null)
                {
                    logger.LogWarning("Ignoring sparkline history {Path}: unsupported schema version {Version}",
                        path, file?.SchemaVersion);
                    continue;
                }

                var config = await configStore.LoadAsync(serial);
                if (config is null) continue;   // device no longer configured

                foreach (var button in config.Pages.OfType<ButtonGridPage>().SelectMany(p => p.Buttons))
                {
                    if (!file.Buttons.TryGetValue(button.ButtonId, out var points) || points is null) continue;
                    if (history.Import(serial, button,
                            points.Select(p => (DateTimeOffset.FromUnixTimeMilliseconds(p.T), p.V))))
                        restored++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ignoring unreadable sparkline history {Path}", path);
            }
        }

        // What was just loaded is what's on disk: no need to write it straight back.
        _savedVersion = history.Version;
        if (restored > 0)
            logger.LogInformation("Restored sparkline history for {Count} button(s)", restored);
    }

    private string PathFor(string serial) => Path.Combine(Directory, $"{serial}.json");

    // Serials are hardware serials from the catalogue; refuse anything that could leave the directory.
    internal static bool IsSafeFileName(string serial) =>
        serial.Length > 0 && serial is not ("." or "..")
        && serial.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) < 0;

    // File format, versioned like the other persisted files. Points are [time, value] pairs in
    // Unix milliseconds (UTC), oldest first.
    private sealed record SparklineFile(int SchemaVersion, long SavedAt, Dictionary<string, List<SparklinePointFile>>? Buttons);
    private sealed record SparklinePointFile(long T, double V);
}
