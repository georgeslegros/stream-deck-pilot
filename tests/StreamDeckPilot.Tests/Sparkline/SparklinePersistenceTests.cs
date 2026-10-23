using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Sparkline;

namespace StreamDeckPilot.Tests.Sparkline;

/// <summary>Sparkline history survives a restart (#144).</summary>
public sealed class SparklinePersistenceTests : IDisposable
{
    private const string Serial = "SN_PERSIST";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly ListLogger _log = new();

    public SparklinePersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "sparklines", $"{Serial}.json");

    private static ButtonDefinition Trend(string id = "co2", double hours = 6) => new(id, 0, "main",
        new DisplaySpec(Center: new TextZone(null, "{value}"), Sparkline: new SparklineSpec(hours)),
        new InboundBinding($"sensors/{id}", null, null, false, null), [],
        new Dictionary<string, IReadOnlyList<ButtonAction>>());

    private async Task<ConfigStore> SaveConfigAsync(params ButtonDefinition[] buttons)
    {
        var store = new ConfigStore(Options.Create(new StorageOptions { BaseDirectory = _dir }));
        await store.SaveAsync(new DeviceConfig(2, Serial, [new ButtonGridPage("main", buttons)]));
        return store;
    }

    private SparklineHistory NewHistory(int maxPoints = 240, TimeProvider? time = null) =>
        new(Options.Create(new SparklineOptions { MaxPoints = maxPoints }), time ?? _time);

    private SparklinePersistence NewPersistence(SparklineHistory history, ConfigStore store, int saveIntervalSeconds = 300,
        TimeProvider? time = null) =>
        new(history, store, Options.Create(new StorageOptions { BaseDirectory = _dir }),
            Options.Create(new SparklineOptions { SaveIntervalSeconds = saveIntervalSeconds }), _log, time ?? _time);

    // Two values an hour apart, then a fresh process loads them.
    private async Task<(SparklineHistory Before, ConfigStore Store)> RecordAndSaveAsync(ButtonDefinition button)
    {
        var store = await SaveConfigAsync(button);
        var history = NewHistory();
        history.Record(Serial, button, "400");
        _time.Advance(TimeSpan.FromHours(1));
        history.Record(Serial, button, "900");
        Assert.True(await NewPersistence(history, store).SaveAsync());
        return (history, store);
    }

    [Fact]
    public async Task SaveThenLoad_RestoresTheTrend()
    {
        var trend = Trend();
        var (before, store) = await RecordAndSaveAsync(trend);
        var expected = before.Refresh(Serial, trend) ?? before.Record(Serial, trend, null);

        var after = NewHistory();
        await NewPersistence(after, store).LoadAsync();

        // A new process sees the same trend as the old one would have at the same moment.
        Assert.Equal(expected!.Points, after.Record(Serial, trend, null)!.Points);
        Assert.Contains(after.Record(Serial, trend, null)!.Points, p => p.Value == 400 && p.X < 1);
    }

    [Fact]
    public async Task Load_DropsPointsOlderThanTheWindow_KeepingTheLeftEdgeValue()
    {
        var trend = Trend(hours: 1);
        var store = await SaveConfigAsync(trend);
        var history = NewHistory();
        history.Record(Serial, trend, "100");
        _time.Advance(TimeSpan.FromMinutes(30));
        history.Record(Serial, trend, "200");
        _time.Advance(TimeSpan.FromMinutes(20));
        history.Record(Serial, trend, "300");
        await NewPersistence(history, store).SaveAsync();

        _time.Advance(TimeSpan.FromMinutes(45));   // down for 45 min: 100 and 200 are now older than 1 h
        var after = NewHistory();
        await NewPersistence(after, store).LoadAsync();

        var points = after.Record(Serial, trend, null)!.Points;
        Assert.DoesNotContain(points, p => p.Value == 100);
        Assert.Equal(200, points[0].Value);   // the one point at or before the window start
        Assert.Equal(0, points[0].X);
        Assert.Contains(points, p => p.Value == 300 && p.X > 0 && p.X < 1);
    }

    [Fact]
    public async Task Load_ResamplesWhenTheWindowShrank()
    {
        var store = await SaveConfigAsync(Trend(hours: 6));
        var history = NewHistory();
        for (var i = 0; i < 60; i++)
        {
            history.Record(Serial, Trend(hours: 6), (400 + i).ToString());
            _time.Advance(TimeSpan.FromMinutes(5));
        }
        await NewPersistence(history, store).SaveAsync();

        // The window became 1 h and MaxPoints 4: 15-min buckets, so at most 5 points come back.
        var shrunk = Trend(hours: 1);
        await SaveConfigAsync(shrunk);
        var after = NewHistory(maxPoints: 4);
        await NewPersistence(after, store).LoadAsync();

        var exported = after.Export()[Serial]["co2"];
        Assert.InRange(exported.Count, 2, 5);
        Assert.All(exported.Skip(1), p => Assert.True(p.At > _time.GetUtcNow() - TimeSpan.FromHours(1)));
        Assert.Equal(459, exported[^1].Value);
    }

    [Fact]
    public async Task Load_DropsButtonsThatAreGoneOrLostTheirSparkline()
    {
        var kept = Trend("co2");
        var removed = Trend("temp");
        var store = await SaveConfigAsync(kept, removed with { KeyIndex = 1 });
        var history = NewHistory();
        history.Record(Serial, kept, "400");
        history.Record(Serial, removed, "21");
        await NewPersistence(history, store).SaveAsync();

        // New config: "temp" is gone; "co2" is still there but no longer has a sparkline.
        await SaveConfigAsync(kept with { Display = kept.Display with { Sparkline = null } });
        var after = NewHistory();
        await NewPersistence(after, store).LoadAsync();

        Assert.Empty(after.Export());
    }

    [Fact]
    public async Task Load_IgnoresFutureDatedPoints()
    {
        var trend = Trend();
        var (_, store) = await RecordAndSaveAsync(trend);
        // The next process's clock is 30 min behind: the 900 recorded "later" is in its future.
        var behind = new FakeTimeProvider(_time.GetUtcNow() - TimeSpan.FromMinutes(30));
        var after = NewHistory(time: behind);
        await NewPersistence(after, store, time: behind).LoadAsync();

        var points = after.Export()[Serial]["co2"];
        Assert.Equal([400.0], points.Select(p => p.Value));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schemaVersion":99,"savedAt":0,"buttons":{}}""")]
    [InlineData("")]
    public async Task Load_CorruptOrOtherVersionFile_StartsEmptyWithAWarning(string content)
    {
        var store = await SaveConfigAsync(Trend());
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, content);

        var history = NewHistory();
        await NewPersistence(history, store).LoadAsync();   // must not throw

        Assert.Empty(history.Export());
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("sparkline history"));
    }

    [Fact]
    public async Task Save_SkipsWhenNothingChanged()
    {
        var trend = Trend();
        var (history, store) = await RecordAndSaveAsync(trend);
        var persistence = NewPersistence(history, store);
        Assert.True(await persistence.SaveAsync());    // first save by this instance
        var written = File.GetLastWriteTimeUtc(FilePath);

        Assert.False(await persistence.SaveAsync());

        history.Record(Serial, trend, null);           // a text/no value changes nothing
        Assert.False(await persistence.SaveAsync());

        _time.Advance(TimeSpan.FromMinutes(5));
        history.Record(Serial, trend, "950");
        Assert.True(await persistence.SaveAsync());
        Assert.True(File.GetLastWriteTimeUtc(FilePath) >= written);
    }

    [Fact]
    public async Task Load_DoesNotMarkTheHistoryAsChanged()
    {
        var (_, store) = await RecordAndSaveAsync(Trend());
        var after = NewHistory();
        var persistence = NewPersistence(after, store);
        await persistence.LoadAsync();

        Assert.False(await persistence.SaveAsync());
    }

    [Fact]
    public async Task Save_RemovesFilesOfDevicesWithNoHistoryLeft()
    {
        var trend = Trend();
        var (history, store) = await RecordAndSaveAsync(trend);
        var persistence = NewPersistence(history, store);

        history.Retain(Serial, new HashSet<string>());
        Assert.True(await persistence.SaveAsync());

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task HostStopping_SavesBeforeOtherServicesStop()
    {
        var trend = Trend();
        var store = await SaveConfigAsync(trend);
        var history = NewHistory();
        var probe = new StopProbe(FilePath);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(probe);   // registered first: stops last
        builder.Services.AddSingleton<IHostedService>(NewPersistence(history, store, saveIntervalSeconds: 0));
        using var host = builder.Build();
        await host.StartAsync();

        history.Record(Serial, trend, "650");
        await host.StopAsync();

        Assert.True(File.Exists(FilePath));
        Assert.True(probe.FileExistedAtStop, "the history must be saved before the other services stop");
    }

    [Fact]
    public async Task HostStarting_LoadsBeforeOtherServicesStart()
    {
        var trend = Trend();
        var (_, store) = await RecordAndSaveAsync(trend);
        var history = NewHistory();
        var probe = new StartProbe(history);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(probe);   // registered first: would start first
        builder.Services.AddSingleton<IHostedService>(NewPersistence(history, store));
        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();

        Assert.True(probe.HadHistoryAtStart, "MQTT and the supervisor must start with the history loaded");
    }

    [Fact]
    public async Task SavesPeriodically()
    {
        var trend = Trend();
        var store = await SaveConfigAsync(trend);
        var history = NewHistory();
        var persistence = NewPersistence(history, store, saveIntervalSeconds: 300);
        history.Record(Serial, trend, "500");
        var start = _time.GetUtcNow();
        await persistence.StartAsync(CancellationToken.None);
        try
        {
            // ExecuteAsync may arm its delay a moment after StartAsync returns, so step the clock
            // until the file appears instead of jumping exactly 300 s.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(FilePath) && DateTime.UtcNow < deadline)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(5);
            }
            Assert.True(File.Exists(FilePath));
            Assert.InRange((_time.GetUtcNow() - start).TotalSeconds, 300, 330);
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
        }
    }

    private sealed class StopProbe(string path) : IHostedService
    {
        public bool FileExistedAtStop { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            FileExistedAtStop = File.Exists(path);
            return Task.CompletedTask;
        }
    }

    private sealed class StartProbe(SparklineHistory history) : IHostedService
    {
        public bool HadHistoryAtStart { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            HadHistoryAtStart = history.Export().Count > 0;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ListLogger : ILogger<SparklinePersistence>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
