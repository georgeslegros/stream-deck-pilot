using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenMacroBoard.SDK;
using StreamDeckPilot.Core;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Core.Validation;
using StreamDeckPilot.Infrastructure.Icons;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Tests.Brightness;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Rendering;

/// <summary>Blink styles (#131): flash/pulse, strength and period, global defaults and per-rule overrides.</summary>
public sealed class BlinkStyleTests
{
    private const string Serial = "SN1";

    private static readonly BlinkSpec Flash1000 = new(BlinkSpec.Flash, PeriodMs: 1000);
    private static readonly BlinkSpec Pulse1200 = new(BlinkSpec.Pulse, PeriodMs: 1200);

    private static ButtonRenderState State(BlinkSpec? blink, string colour = "#CC0000", string value = "42") =>
        new("b", colour, null, IconPlacement.Corner, value, null, DependsOnBroker: true, Blink: blink);

    private static (BlinkScheduler Scheduler, DesiredStateStore Store, ManualTimeProvider Time) Build(BlinkOptions? options = null)
    {
        var store = new DesiredStateStore();
        var time = new ManualTimeProvider();
        return (new BlinkScheduler(store, Options.Create(options ?? new BlinkOptions()), time), store, time);
    }

    private static void Tick(ManualTimeProvider time) => time.Timers[^1].FireStale();

    // ── JSON ─────────────────────────────────────────────────────────────────

    private static ConditionalRule ReadRule(string blinkJson) =>
        JsonSerializer.Deserialize<ConditionalRule>(
            $$"""{ "condition": ">1000", "backgroundColour": "#CC3300", "icon": null, "blink": {{blinkJson}} }""", JsonOptions.Default)!;

    [Fact]
    public void BlinkTrue_StillReadsAsTheDefaults_AndWritesBackAsTrue()
    {
        var rule = ReadRule("true");

        Assert.Same(BlinkSpec.On, rule.Blink);
        Assert.Contains("\"blink\": true", JsonSerializer.Serialize(rule, JsonOptions.Default));
    }

    [Fact]
    public void BlinkFalse_ReadsAsNoBlink_AndIsLeftOut()
    {
        var rule = ReadRule("false");

        Assert.Null(rule.Blink);
        Assert.DoesNotContain("blink", JsonSerializer.Serialize(rule, JsonOptions.Default));
    }

    [Fact]
    public void BlinkObject_RoundTrips_WithOnlyTheSetProperties()
    {
        var rule = ReadRule("""{ "style": "pulse", "strength": 0.6, "periodMs": 2000 }""");

        Assert.Equal(new BlinkSpec("pulse", 0.6, 2000), rule.Blink);
        var json = JsonSerializer.Serialize(rule, JsonOptions.Default);
        Assert.Contains("\"style\": \"pulse\"", json);
        Assert.Equal(rule, JsonSerializer.Deserialize<ConditionalRule>(json, JsonOptions.Default));

        var partial = JsonSerializer.Serialize(ReadRule("""{ "style": "pulse" }"""), JsonOptions.Default);
        Assert.DoesNotContain("strength", partial);
        Assert.DoesNotContain("periodMs", partial);
    }

    [Fact]
    public void EmptyBlinkObject_MeansTheDefaults_AndWritesBackAsTrue()
    {
        var rule = ReadRule("{}");

        Assert.Equal(BlinkSpec.On, rule.Blink);
        Assert.Contains("\"blink\": true", JsonSerializer.Serialize(rule, JsonOptions.Default));
    }

    [Fact]
    public void BlinkOfAnotherType_IsAJsonError()
    {
        Assert.Throws<JsonException>(() => ReadRule("\"pulse\""));
    }

    [Fact]
    public void ExistingConfigWithBlinkTrue_RoundTripsByteIdentical()
    {
        var config = new DeviceConfig(SchemaVersions.ConfigCurrentVersion, "EXAMPLESERIAL1",
        [
            new ButtonGridPage("main",
            [
                new ButtonDefinition("co2", 0, "main", new DisplaySpec(),
                    Inbound: null,
                    Rules: [new(">1200", "#CC3300", null, BlinkSpec.On), new("any", "#226622", null)],
                    Gestures: new Dictionary<string, IReadOnlyList<ButtonAction>>()),
            ]),
        ]);
        var json = JsonSerializer.Serialize(config, JsonOptions.Default);

        var again = JsonSerializer.Serialize(JsonSerializer.Deserialize<DeviceConfig>(json, JsonOptions.Default), JsonOptions.Default);

        Assert.Equal(json, again);
        Assert.Contains("\"blink\": true", json);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private static readonly DeviceEntry Mk2 = new("EXAMPLESERIAL1", "Stream Deck MK.2", 3, 5, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static IReadOnlyList<string> Errors(BlinkSpec blink)
    {
        var config = new DeviceConfig(SchemaVersions.ConfigCurrentVersion, Mk2.Serial,
        [
            new ButtonGridPage("main",
            [
                new ButtonDefinition("co2", 0, "main", new DisplaySpec(), Inbound: null,
                    Rules: [new(">1200", "#CC3300", null, blink)],
                    Gestures: new Dictionary<string, IReadOnlyList<ButtonAction>>()),
            ]),
        ]);
        return ConfigValidator.ValidateConfig(config, Mk2).Errors;
    }

    [Fact]
    public void Validator_AcceptsTrueAndAFullOverride()
    {
        Assert.Empty(Errors(BlinkSpec.On));
        Assert.Empty(Errors(new BlinkSpec("pulse", 0.6, 2000)));
        Assert.Empty(Errors(new BlinkSpec("flash", BlinkSpec.MinStrength, BlinkSpec.MinPeriodMs)));
        Assert.Empty(Errors(new BlinkSpec("flash", BlinkSpec.MaxStrength, BlinkSpec.MaxPeriodMs)));
    }

    [Fact]
    public void Validator_HintsTheCaseOfAStyle()
    {
        var error = Assert.Single(Errors(new BlinkSpec("Pulse")));
        Assert.Contains("blink.style 'Pulse' must be written 'pulse'", error);
    }

    [Fact]
    public void Validator_RejectsAnUnknownStyle()
    {
        var error = Assert.Single(Errors(new BlinkSpec("strobe")));
        Assert.Contains("blink.style 'strobe' is not supported (supported: flash, pulse)", error);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.95)]
    [InlineData(double.NaN)]
    public void Validator_RejectsAStrengthOutOfRange(double strength)
    {
        var error = Assert.Single(Errors(new BlinkSpec(Strength: strength)));
        Assert.Contains("blink.strength must be between 0.1 and 0.9", error);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(20_000)]
    public void Validator_RejectsAPeriodOutOfRange(int period)
    {
        var error = Assert.Single(Errors(new BlinkSpec(PeriodMs: period)));
        Assert.Contains("blink.periodMs must be between 400 and 10000", error);
    }

    // ── Defaults and overrides ───────────────────────────────────────────────

    [Fact]
    public void Defaults_AreAFlashAtAQuarter_EverySecond()
    {
        var (scheduler, _, _) = Build();

        var blink = scheduler.Resolve(BlinkSpec.On);

        Assert.Equal(new BlinkScheduler.ResolvedBlink(false, 0.25f, 1000, 2), blink);
    }

    [Fact]
    public void PulseWithoutAStrength_UsesTheMilderPulseDefault()
    {
        var (scheduler, _, _) = Build();

        Assert.Equal(0.6f, scheduler.Resolve(new BlinkSpec(BlinkSpec.Pulse)).Strength);
    }

    [Fact]
    public void PerRuleOverride_WinsOverTheGlobalDefaults()
    {
        var (scheduler, _, _) = Build(new BlinkOptions { Style = BlinkSpec.Pulse, PulseStrength = 0.5, PulsePeriodMilliseconds = 3000 });

        Assert.Equal(new BlinkScheduler.ResolvedBlink(true, 0.5f, 3000, 12), scheduler.Resolve(BlinkSpec.On));
        Assert.Equal(new BlinkScheduler.ResolvedBlink(false, 0.3f, 800, 2),
            scheduler.Resolve(new BlinkSpec(BlinkSpec.Flash, 0.3, 800)));
    }

    [Fact]
    public void FlashWithoutAPeriod_UsesTheFlashPeriod()
    {
        var (scheduler, _, _) = Build(new BlinkOptions { PeriodMilliseconds = 1200, PulsePeriodMilliseconds = 3000 });

        Assert.Equal(1200, scheduler.Resolve(new BlinkSpec(BlinkSpec.Flash)).PeriodMs);
        Assert.Equal(1000, Build().Scheduler.Resolve(new BlinkSpec(BlinkSpec.Flash)).PeriodMs);
    }

    [Fact]
    public void PulseWithoutAPeriod_UsesTheSlowerPulsePeriod_NotTheLegacyInterval()
    {
        Assert.Equal(2000, Build().Scheduler.Resolve(new BlinkSpec(BlinkSpec.Pulse)).PeriodMs);

        // The legacy interval only ever meant half a flash.
        var (scheduler, _, _) = Build(new BlinkOptions { IntervalMilliseconds = 300 });
        Assert.Equal(600, scheduler.Resolve(new BlinkSpec(BlinkSpec.Flash)).PeriodMs);
        Assert.Equal(2000, scheduler.Resolve(new BlinkSpec(BlinkSpec.Pulse)).PeriodMs);
    }

    [Fact]
    public void OldIntervalSetting_IsHalfThePeriod_UnlessAPeriodIsSet()
    {
        Assert.Equal(600, new BlinkOptions { IntervalMilliseconds = 300 }.EffectivePeriodMilliseconds);
        Assert.Equal(2000, new BlinkOptions { IntervalMilliseconds = 300, PeriodMilliseconds = 2000 }.EffectivePeriodMilliseconds);
        Assert.Equal(1000, new BlinkOptions().EffectivePeriodMilliseconds);
    }

    [Fact]
    public void ShortPulse_NeverGoesUnderTheFrameFloor()
    {
        var (scheduler, _, _) = Build();

        var blink = scheduler.Resolve(new BlinkSpec(BlinkSpec.Pulse, PeriodMs: 400));

        Assert.Equal(4, blink.Steps);
        Assert.True(blink.StepMs >= BlinkOptions.MinFrameMilliseconds);
    }

    [Fact]
    public void PulseLevels_FadeFromNormalToTheStrengthAndBack()
    {
        var (scheduler, _, _) = Build();
        var blink = scheduler.Resolve(Pulse1200);

        var levels = Enumerable.Range(0, blink.Steps).Select(blink.LevelAt).ToList();

        Assert.Equal(1f, levels[0]);
        Assert.Equal(0.6f, levels[6]);
        for (var i = 1; i <= 6; i++) Assert.True(levels[i] < levels[i - 1]);   // fading out
        for (var i = 7; i < 12; i++) Assert.True(levels[i] > levels[i - 1]);   // fading back in
        Assert.Equal(levels[5], levels[7]);                                     // symmetric: shared frames
    }

    // ── Scheduler: ticks and repaints ────────────────────────────────────────

    // What the supervisor does on a tick, for keys 0.. on the active page: repaint (and record the
    // frame, as the renderer does) only the keys whose frame changed.
    private static Dictionary<int, int> RunCycle(BlinkScheduler scheduler, ManualTimeProvider time, int ticks,
        params BlinkSpec[] keys)
    {
        var repaints = keys.Select((_, i) => i).ToDictionary(i => i, _ => 0);
        for (var t = 0; t < ticks; t++)
        {
            Tick(time);
            for (var k = 0; k < keys.Length; k++)
            {
                if (!scheduler.ShouldRepaint(Serial, k, keys[k])) continue;
                scheduler.PaintLevel(Serial, k, keys[k]);
                repaints[k]++;
            }
        }
        return repaints;
    }

    [Fact]
    public void Pulse_RepaintsABoundedNumberOfFramesPerCycle()
    {
        var (scheduler, store, time) = Build();
        store.Set(Serial, "main", 0, State(Pulse1200));
        scheduler.PaintLevel(Serial, 0, Pulse1200); // the initial paint

        Assert.Equal(TimeSpan.FromMilliseconds(100), scheduler.TickInterval(Serial));
        var repaints = RunCycle(scheduler, time, ticks: 12, Pulse1200);

        Assert.Equal(12, repaints[0]);
        Assert.InRange(repaints[0], 1, BlinkOptions.MaxPulseSteps);
    }

    [Fact]
    public void FlashAndPulseWithDifferentPeriods_Coexist_EachRepaintedOnItsOwnFrames()
    {
        var (scheduler, store, time) = Build();
        store.Set(Serial, "main", 0, State(Flash1000));
        Assert.Equal(TimeSpan.FromMilliseconds(500), scheduler.TickInterval(Serial));
        store.Set(Serial, "main", 1, State(Pulse1200));
        scheduler.PaintLevel(Serial, 0, Flash1000);
        scheduler.PaintLevel(Serial, 1, Pulse1200);

        // The device ticks at the finest frame needed (the pulse's 100 ms) ...
        Assert.Equal(TimeSpan.FromMilliseconds(100), scheduler.TickInterval(Serial));
        Assert.Equal(TimeSpan.FromMilliseconds(100), time.Timers[^1].DueTime);

        var repaints = RunCycle(scheduler, time, ticks: 12, Flash1000, Pulse1200);

        // ... but the flash key is only repainted when its own half changes (at 500 and 1000 ms).
        Assert.Equal(2, repaints[0]);
        Assert.Equal(12, repaints[1]);
        Assert.Equal(0.25f, scheduler.Level(Serial, new BlinkSpec(BlinkSpec.Flash, PeriodMs: 2000)), 3); // 1200 ms: second half of a 2 s flash
    }

    [Fact]
    public void Timer_RetunesWhenTheFastestKeyStops_AndStopsWhenNothingBlinks()
    {
        var (scheduler, store, time) = Build();
        store.Set(Serial, "main", 0, State(Flash1000));
        store.Set(Serial, "main", 1, State(Pulse1200));
        Assert.Equal(TimeSpan.FromMilliseconds(100), scheduler.TickInterval(Serial));

        store.Set(Serial, "main", 1, State(null));
        Assert.Equal(TimeSpan.FromMilliseconds(500), scheduler.TickInterval(Serial));
        Assert.Equal(TimeSpan.FromMilliseconds(500), time.Timers[^1].DueTime);

        store.Set(Serial, "main", 0, State(null));
        Assert.False(scheduler.IsRunning(Serial));
        Assert.Null(scheduler.TickInterval(Serial));
        Assert.Null(time.Timers[^1].DueTime); // disposed
        Assert.Equal(1f, scheduler.Level(Serial, Pulse1200));
    }

    // ── Renderer: frame cache and precedence ─────────────────────────────────

    private static KeyBitmapComposer Composer(string dir) =>
        new(new IconResolver(new CustomImageSource(Options.Create(new StorageOptions { BaseDirectory = dir })),
            NullLogger<IconResolver>.Instance));

    [Fact]
    public void Renderer_ComposesAPulsingKeyOnce_AndDerivesEveryFrameFromIt()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var (scheduler, store, time) = Build();
            var renderer = new DeviceRenderer(Composer(dir), blink: scheduler);
            var board = new FakeMacroBoard();
            store.Set(Serial, "main", 0, State(Pulse1200));

            for (var t = 0; t < 24; t++) // two full cycles
            {
                renderer.RenderLatest(board, Serial, "main", 0, store);
                Tick(time);
            }

            Assert.Equal(1, renderer.BlinkFrameCompositions);
            Assert.Equal(24, board.RenderCalls.Count);
            Assert.Same(board.RenderCalls[1].Bitmap, board.RenderCalls[13].Bitmap); // same frame, next cycle: cached
            Assert.NotEqual(board.RenderCalls[0].Bitmap, board.RenderCalls[6].Bitmap); // normal vs dimmest

            // A new value is a new key state: composed again, once.
            store.Set(Serial, "main", 0, State(Pulse1200, value: "43"));
            renderer.RenderLatest(board, Serial, "main", 0, store);
            Tick(time);
            renderer.RenderLatest(board, Serial, "main", 0, store);
            Assert.Equal(2, renderer.BlinkFrameCompositions);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Renderer_PressFeedbackWins_OverAPulseFrame()
    {
        var (scheduler, store, time) = Build();
        var feedback = new PressFeedbackTracker(new PressFeedbackOptions(), time);
        var renderer = new DeviceRenderer(blink: scheduler, feedback: feedback);
        var board = new FakeMacroBoard();
        var button = new ButtonDefinition("b", 0, "main", new DisplaySpec(),
            new InboundBinding("t/state", null, null, false, null), [], new Dictionary<string, IReadOnlyList<ButtonAction>>
            {
                [GestureNames.Tap] = [new PublishAction("t/set", "on")],
            });
        store.Set(Serial, "main", 0, State(Pulse1200));
        for (var t = 0; t < 6; t++) Tick(time); // the dimmest frame

        renderer.RenderLatest(board, Serial, "main", 0, store);
        feedback.Pressed(Serial, "main", button);
        renderer.RenderLatest(board, Serial, "main", 0, store);

        Assert.Equal(KeyBitmap.Create.FromRgb(30, 30, 30), board.RenderCalls[0].Bitmap); // dimmed
        Assert.Equal(KeyBitmap.Create.FromRgb(255, 255, 255), board.RenderCalls[1].Bitmap); // feedback, full look
    }
}
