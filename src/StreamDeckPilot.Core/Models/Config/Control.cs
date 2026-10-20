using System.Text.Json.Serialization;

namespace StreamDeckPilot.Core.Models.Config;

/// <summary>
/// A control occupies several keys of a page and behaves as one unit (#132). Typed by a
/// discriminator, like pages and actions, so new control types need no schema change.
/// The keys a control uses can't also hold a button.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetpointControl), "Setpoint")]
public abstract record Control(string ControlId);

/// <summary>Which way a setpoint's three keys run from its anchor key.</summary>
public enum SetpointOrientation
{
    /// <summary>anchor, anchor + columns, anchor + 2·columns: + / value / −.</summary>
    Vertical,

    /// <summary>anchor, anchor + 1, anchor + 2: − / value / +.</summary>
    Horizontal,
}

/// <summary>
/// Three keys (−, value, +) that adjust a numeric target such as a thermostat setpoint. +/− change
/// a local pending target, shown at once; <see cref="SetpointCommit.DelayMs"/> after the last press
/// one absolute value is published; the pending look stays until <see cref="Target"/> reports it.
/// <see cref="Rules"/> colour the value key from the CURRENT value, like a button's rules.
/// </summary>
public record SetpointControl(
    string ControlId,
    int AnchorKey,
    SetpointOrientation Orientation,
    SetpointBinding Current,
    SetpointBinding Target,
    SetpointCommit Commit,
    double Step,
    double Min,
    double Max,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Precision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ConfirmTimeoutSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ConditionalRule>? Rules = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LongPressStep = null)
    : Control(ControlId)
{
    public const int MinConfirmTimeoutSeconds = 1;
    public const int MaxConfirmTimeoutSeconds = 600;

    /// <summary>
    /// The keys this control occupies on a grid <paramref name="columns"/> wide, as (minus, value, plus).
    /// Says nothing about whether they fit the grid: see <see cref="Validation.SetpointGeometry"/>.
    /// </summary>
    public (int Minus, int Value, int Plus) Keys(int columns) => Orientation == SetpointOrientation.Vertical
        ? (AnchorKey + 2 * columns, AnchorKey + columns, AnchorKey)
        : (AnchorKey, AnchorKey + 1, AnchorKey + 2);
}

/// <summary>A topic a setpoint reads a number from. ValueField works as for buttons (default "value").</summary>
public record SetpointBinding(
    string Topic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueField = null,
    // Only read on Current: the value key dims as stale when nothing arrives for this long.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeSpan? StalenessTimeout = null);

/// <summary>
/// Where the new target goes. Payload is a template: {target} is replaced by the number (invariant
/// culture, rounded to the step); null publishes the bare number. DelayMs is the debounce after the
/// last press (null: Setpoint:DefaultCommitDelayMs).
/// </summary>
public record SetpointCommit(
    string Topic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payload = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DelayMs = null)
{
    public const int MaxDelayMs = 10_000;
    public const string TargetPlaceholder = "{target}";
}
