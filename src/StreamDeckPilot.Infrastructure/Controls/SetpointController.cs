using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Core.Rules;
using StreamDeckPilot.Infrastructure.Mqtt;
using StreamDeckPilot.Infrastructure.Mqtt.Pipeline;
using StreamDeckPilot.Infrastructure.Rendering;

namespace StreamDeckPilot.Infrastructure.Controls;

/// <summary>
/// Runs every setpoint control (#132): three keys (−, value, +) adjusting one numeric target.
/// <list type="bullet">
/// <item>+/− move a local pending target (from the pending, else reported, else current value), snapped
/// to the step and clamped to min/max. It shows at once on the value key, on a band in the pending colour.</item>
/// <item><c>commit.delayMs</c> after the last press, ONE message with the absolute target is published.
/// If that target is what the device already reports, nothing is published and the pending look clears.</item>
/// <item>The pending look stays until the target topic reports the committed value (within half a step).
/// A target reported before the commit doesn't count. No match within the confirm timeout: the value key
/// flashes the failed look (#22), a warning is logged, and the reported target shows again.</item>
/// <item>State is per device and control, kept across page switches: a commit still happens off screen.</item>
/// </list>
/// The keys' looks are written to <see cref="DesiredStateStore"/>; <see cref="Changed"/> asks for a repaint,
/// which the MQTT service does only when the control's page is on screen.
/// </summary>
public sealed class SetpointController : IDisposable
{
    private enum Phase { Idle, Adjusting, AwaitingConfirm, Failed }

    private sealed class State(string serial)
    {
        public string Serial { get; } = serial;
        public required string PageId { get; set; }
        public required SetpointControl Control { get; set; }
        public int? Columns { get; set; }              // device width, known once the deck has been seen
        public double? Current { get; set; }
        public string? CurrentRaw { get; set; }        // as received: rules match on it like on a button value
        public DateTimeOffset? CurrentAt { get; set; }
        public bool Stale { get; set; }
        public double? ReportedTarget { get; set; }
        public double? Pending { get; set; }           // the target shown while adjusting or awaiting confirmation
        public double Committed { get; set; }
        public Phase Phase { get; set; }
        public long Generation { get; set; }           // a late timer callback from an earlier cycle is ignored
        public ITimer? Timer { get; set; }
    }

    private readonly DesiredStateStore _desired;
    private readonly SetpointOptions _opts;
    private readonly TimeProvider _time;
    private readonly BrokerIndicator? _broker;
    private readonly ILogger<SetpointController>? _logger;
    private readonly Dictionary<(string Serial, string ControlId), State> _states = new();
    private readonly object _lock = new();

    public SetpointController(DesiredStateStore desired, IOptions<SetpointOptions>? options = null,
        TimeProvider? timeProvider = null, BrokerIndicator? broker = null, ILogger<SetpointController>? logger = null)
    {
        _desired = desired;
        _opts = options?.Value ?? new SetpointOptions();
        _time = timeProvider ?? TimeProvider.System;
        _broker = broker;
        _logger = logger;
    }

    /// <summary>Raised (outside the lock) with (serial, pageId, keyIndex) when a key's look changed.</summary>
    public event Action<string, string, int>? Changed;

    /// <summary>Sends (topic, payload); true when it went out. Set by the MQTT service.</summary>
    public Func<string, string, Task<bool>>? Publish { get; set; }

    // ── Config ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The device's config was (re)loaded: registers its controls, forgets removed ones, and writes the
    /// look of every control key to desired state. <paramref name="columns"/> is the deck's width, null
    /// when it isn't connected (the keys are written when it is). Raises no <see cref="Changed"/>: the
    /// caller repaints the page.
    /// </summary>
    public void Seed(string serial, DeviceConfig config, int? columns)
    {
        lock (_lock)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (pageId, control) in Setpoints(config))
            {
                present.Add(control.ControlId);
                var state = Register(serial, pageId, control);
                if (columns is not null) state.Columns = columns;
                WriteKeys(state);
            }

            foreach (var key in _states.Keys.Where(k => k.Serial == serial && !present.Contains(k.ControlId)).ToList())
            {
                _states[key].Timer?.Dispose();
                _states.Remove(key);
            }
        }
    }

    /// <summary>The setpoint key at this position, if any: which control, which role.</summary>
    public bool TryFindKey(string serial, string pageId, int keyIndex, out SetpointControl control, out SetpointKeyRole role)
    {
        lock (_lock)
        {
            foreach (var state in _states.Values)
            {
                if (state.Serial != serial || state.PageId != pageId || state.Columns is not { } columns) continue;
                var (minus, value, plus) = state.Control.Keys(columns);
                if (keyIndex != minus && keyIndex != value && keyIndex != plus) continue;
                role = keyIndex == minus ? SetpointKeyRole.Minus : keyIndex == plus ? SetpointKeyRole.Plus : SetpointKeyRole.Value;
                control = state.Control;
                return true;
            }
        }
        control = null!;
        role = default;
        return false;
    }

    // ── Inbound ──────────────────────────────────────────────────────────────

    /// <summary>A message arrived on the control's current (<paramref name="isTarget"/> false) or target topic.</summary>
    public void ValueReceived(string serial, string pageId, SetpointControl control, bool isTarget, string payload)
    {
        var binding = isTarget ? control.Target : control.Current;
        var (raw, _, _) = InboundPipeline.Extract(payload, binding?.ValueField, null, null);
        if (!RuleCondition.TryParseNumber(raw, out var value))
        {
            _logger?.LogDebug("No number in payload on {Topic} for setpoint {ControlId} — ignored", binding?.Topic, control.ControlId);
            return;
        }

        List<int> keys;
        State state;
        lock (_lock)
        {
            state = Register(serial, pageId, control);
            if (isTarget)
            {
                state.ReportedTarget = value;
                if (state.Phase == Phase.AwaitingConfirm && Matches(value, state.Committed, state.Control))
                {
                    _logger?.LogInformation("Setpoint {ControlId} on {Serial} confirmed at {Target}", control.ControlId, serial, value);
                    EndCycle(state);
                }
            }
            else
            {
                state.Current = value;
                state.CurrentRaw = raw;
                state.CurrentAt = _time.GetUtcNow();
                state.Stale = false;
            }
            keys = WriteKeys(state);
        }
        RaiseChanged(state, keys);
    }

    /// <summary>
    /// Re-evaluates staleness of every control's current value (called by the staleness monitor);
    /// the value key dims when nothing arrived within <c>current.stalenessTimeout</c>.
    /// </summary>
    public void CheckStaleness()
    {
        var changed = new List<(State, List<int>)>();
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var state in _states.Values)
            {
                if (state.Control.Current?.StalenessTimeout is not { } timeout || state.CurrentAt is not { } at) continue;
                var stale = now - at > timeout;
                if (stale == state.Stale) continue;
                state.Stale = stale;
                changed.Add((state, WriteKeys(state)));
            }
        }
        foreach (var (state, keys) in changed) RaiseChanged(state, keys);
    }

    // ── Presses ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A gesture on one of the control's keys. The caller only forwards presses while the broker is
    /// connected (offline, the offline look explains why nothing happens).
    /// </summary>
    public void Press(string serial, string pageId, int keyIndex, string gesture)
    {
        if (!TryFindKey(serial, pageId, keyIndex, out var control, out var role) || role == SetpointKeyRole.Value) return;

        List<int> keys;
        State state;
        lock (_lock)
        {
            if (!_states.TryGetValue((serial, control.ControlId), out state!)) return;
            var c = state.Control;
            var from = state.Pending ?? state.ReportedTarget ?? state.Current;
            if (from is null)
            {
                _logger?.LogInformation("Setpoint {ControlId} on {Serial}: no target or current value known yet — press ignored",
                    c.ControlId, serial);
                return;
            }

            var step = gesture == GestureNames.LongPress && c.LongPressStep is { } longStep ? longStep : c.Step;
            var next = Snap(from.Value + (role == SetpointKeyRole.Plus ? step : -step), c);
            if (Same(next, from.Value)) return; // already at that end of the range

            state.Pending = next;
            state.Phase = Phase.Adjusting;
            var generation = Rearm(state, CommitDelay(c), g => Commit(state, g));
            _logger?.LogDebug("Setpoint {ControlId} on {Serial}: pending target {Target} (generation {Generation})",
                c.ControlId, serial, next, generation);
            keys = WriteKeys(state);
        }
        RaiseChanged(state, keys);
    }

    // The debounce ended: publish the final target once, then wait for the device to report it.
    private void Commit(State state, long generation)
    {
        string? topic = null, payload = null;
        long awaiting = 0;
        List<int> keys = [];
        lock (_lock)
        {
            if (state.Generation != generation || state.Phase != Phase.Adjusting || state.Pending is not { } target) return;
            var c = state.Control;

            if (state.ReportedTarget is { } reported && Matches(reported, target, c))
            {
                // Back where the device already is: a command would change nothing, and a device that
                // only publishes on change would never confirm it.
                EndCycle(state);
                keys = WriteKeys(state);
            }
            else
            {
                state.Committed = target;
                state.Phase = Phase.AwaitingConfirm;
                awaiting = Rearm(state, ConfirmTimeout(c), g => ConfirmTimedOut(state, g));
                topic = c.Commit.Topic;
                payload = Payload(c, target);
            }
        }

        if (topic is null)
            RaiseChanged(state, keys);
        else
            _ = SendAsync(state, awaiting, topic, payload!);
    }

    private async Task SendAsync(State state, long generation, string topic, string payload)
    {
        var sent = false;
        try { sent = Publish is { } publish && await publish(topic, payload); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Setpoint {ControlId}: publish to {Topic} failed", state.Control.ControlId, topic); }
        if (sent)
        {
            _logger?.LogInformation("Setpoint {ControlId} on {Serial}: published target {Payload} to {Topic}",
                state.Control.ControlId, state.Serial, payload, topic);
            return;
        }

        // Not sent (broker gone): drop the pending target quietly; the offline look explains it.
        List<int> keys;
        lock (_lock)
        {
            if (state.Generation != generation || state.Phase != Phase.AwaitingConfirm) return;
            EndCycle(state);
            keys = WriteKeys(state);
        }
        RaiseChanged(state, keys);
    }

    private void ConfirmTimedOut(State state, long generation)
    {
        List<int> keys;
        bool failed;
        lock (_lock)
        {
            if (state.Generation != generation || state.Phase != Phase.AwaitingConfirm) return;
            // If the broker went away meanwhile, that's not the device's failure: no flash.
            failed = _broker?.ShowOffline != true;
            EndCycle(state);
            if (failed)
            {
                state.Phase = Phase.Failed;
                Rearm(state, TimeSpan.FromMilliseconds(_opts.FailedFlashMilliseconds), g => FlashEnded(state, g));
            }
            keys = WriteKeys(state);
        }
        if (failed)
            _logger?.LogWarning("Setpoint {ControlId} on {Serial}: target {Target} not confirmed on {Topic} within the timeout",
                state.Control.ControlId, state.Serial, state.Committed, state.Control.Target?.Topic);
        RaiseChanged(state, keys);
    }

    private void FlashEnded(State state, long generation)
    {
        List<int> keys;
        lock (_lock)
        {
            if (state.Generation != generation || state.Phase != Phase.Failed) return;
            state.Phase = Phase.Idle;
            state.Timer?.Dispose();
            state.Timer = null;
            keys = WriteKeys(state);
        }
        RaiseChanged(state, keys);
    }

    // ── Helpers (call under _lock) ───────────────────────────────────────────

    private State Register(string serial, string pageId, SetpointControl control)
    {
        if (!_states.TryGetValue((serial, control.ControlId), out var state))
        {
            state = new State(serial) { PageId = pageId, Control = control };
            _states[(serial, control.ControlId)] = state;
            return state;
        }

        // Re-registered from a new config: values read from topics that changed no longer apply.
        if (state.Control.Current?.Topic != control.Current?.Topic || state.Control.Current?.ValueField != control.Current?.ValueField)
        {
            state.Current = null;
            state.CurrentRaw = null;
            state.CurrentAt = null;
            state.Stale = false;
        }
        if (state.Control.Target?.Topic != control.Target?.Topic || state.Control.Target?.ValueField != control.Target?.ValueField)
            state.ReportedTarget = null;
        state.Control = control;
        state.PageId = pageId;
        return state;
    }

    private long Rearm(State state, TimeSpan due, Action<long> callback)
    {
        state.Timer?.Dispose();
        var generation = ++state.Generation;
        state.Timer = _time.CreateTimer(_ => callback(generation), null, due, Timeout.InfiniteTimeSpan);
        return generation;
    }

    private static void EndCycle(State state)
    {
        state.Timer?.Dispose();
        state.Timer = null;
        state.Generation++;
        state.Pending = null;
        state.Phase = Phase.Idle;
    }

    // Writes the three keys' looks; returns the keys written (none until the deck's width is known).
    private List<int> WriteKeys(State state)
    {
        if (state.Columns is not { } columns) return [];
        var c = state.Control;
        var (minus, value, plus) = c.Keys(columns);
        var decimals = c.Precision ?? StepDecimals(c.Step);
        var shown = state.Pending ?? state.ReportedTarget;
        var reference = shown ?? state.Current;

        var rule = InboundPipeline.MatchRule(state.CurrentRaw, c.Rules ?? []);
        _desired.Set(state.Serial, state.PageId, value, new ButtonRenderState(
            c.ControlId,
            rule?.BackgroundColour,
            rule?.Icon,
            IconPlacement.Corner,
            CenterText: null,
            BottomText: null,
            IsDimmed: state.Stale,
            DependsOnBroker: true,
            Blink: rule?.Blink,
            Feedback: state.Phase == Phase.Failed ? PressFeedback.Failed : PressFeedback.None,
            Setpoint: new SetpointFace(SetpointKeyRole.Value,
                Current: state.Current is { } current ? Format(current, decimals) + c.Unit : null,
                Target: shown is { } target ? Format(target, decimals) + c.Unit : null,
                Label: c.Label,
                Pending: state.Pending is not null,
                PendingColour: _opts.PendingColour)));

        _desired.Set(state.Serial, state.PageId, minus, ArrowKey(c, SetpointKeyRole.Minus, "builtin:minus-thick",
            disabled: reference is { } r1 && r1 <= c.Min + Epsilon(c)));
        _desired.Set(state.Serial, state.PageId, plus, ArrowKey(c, SetpointKeyRole.Plus, "builtin:plus-thick",
            disabled: reference is { } r2 && r2 >= c.Max - Epsilon(c)));
        return [minus, value, plus];
    }

    private static ButtonRenderState ArrowKey(SetpointControl c, SetpointKeyRole role, string icon, bool disabled) =>
        new($"{c.ControlId}:{(role == SetpointKeyRole.Plus ? "plus" : "minus")}", null, icon, IconPlacement.Center,
            CenterText: null, BottomText: null, DependsOnBroker: true,
            Setpoint: new SetpointFace(role, Disabled: disabled));

    private void RaiseChanged(State state, List<int> keys)
    {
        foreach (var key in keys) Changed?.Invoke(state.Serial, state.PageId, key);
    }

    private static IEnumerable<(string PageId, SetpointControl Control)> Setpoints(DeviceConfig config) =>
        config.Pages.OfType<ButtonGridPage>()
            .SelectMany(p => (p.Controls ?? []).OfType<SetpointControl>().Select(c => (p.PageId, c)));

    private TimeSpan CommitDelay(SetpointControl c) =>
        TimeSpan.FromMilliseconds(Math.Max(0, c.Commit?.DelayMs ?? _opts.DefaultCommitDelayMs));

    private TimeSpan ConfirmTimeout(SetpointControl c) =>
        TimeSpan.FromSeconds(Math.Max(0, c.ConfirmTimeoutSeconds ?? _opts.DefaultConfirmTimeoutSeconds));

    /// <summary>Snaps to the step grid that starts at min, then clamps to min/max.</summary>
    internal static double Snap(double value, SetpointControl c)
    {
        var snapped = c.Min + Math.Round((value - c.Min) / c.Step, MidpointRounding.AwayFromZero) * c.Step;
        // Clamping last: the top of a range the step doesn't divide is still reachable.
        return Math.Round(Math.Clamp(snapped, c.Min, c.Max), 10);
    }

    /// <summary>The payload for a committed target: the template with {target} filled in, or the bare number.</summary>
    internal static string Payload(SetpointControl c, double target)
    {
        var number = Math.Round(target, StepDecimals(c.Step)).ToString(CultureInfo.InvariantCulture);
        return c.Commit.Payload is { } template ? template.Replace(SetpointCommit.TargetPlaceholder, number) : number;
    }

    // Decimals needed to write any multiple of the step (0.5 → 1, 1 → 0, 0.25 → 2), capped at 3.
    private static int StepDecimals(double step)
    {
        for (var decimals = 0; decimals < DisplaySpec.MaxPrecision; decimals++)
            if (Math.Abs(step * Math.Pow(10, decimals) - Math.Round(step * Math.Pow(10, decimals))) < 1e-9)
                return decimals;
        return DisplaySpec.MaxPrecision;
    }

    private static string Format(double value, int decimals) =>
        InboundPipeline.FormatValue(value.ToString("R", CultureInfo.InvariantCulture), decimals);

    // A reported target confirms a committed one within half a step (devices round differently).
    private static bool Matches(double reported, double committed, SetpointControl c) =>
        Math.Abs(reported - committed) <= c.Step / 2 + Epsilon(c);

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static double Epsilon(SetpointControl c) => c.Step * 1e-6;

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var state in _states.Values) state.Timer?.Dispose();
            _states.Clear();
        }
    }
}
