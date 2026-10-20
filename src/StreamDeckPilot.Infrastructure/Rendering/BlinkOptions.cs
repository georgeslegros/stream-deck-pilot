using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Infrastructure.Rendering;

/// <summary>
/// Global blink defaults (#23, #131). A rule's <c>"blink": true</c> uses these; a rule's blink object
/// overrides any of them (see <see cref="BlinkSpec"/>).
/// </summary>
public record BlinkOptions
{
    /// <summary>Floor for half a flash cycle, so a typo can't flood the USB link with repaints.</summary>
    public const int MinIntervalMilliseconds = BlinkSpec.MinPeriodMs / 2;

    /// <summary>Shortest time between two pulse frames.</summary>
    public const int MinFrameMilliseconds = 100;

    /// <summary>Most frames in one pulse cycle.</summary>
    public const int MaxPulseSteps = 12;

    /// <summary>"flash" (hard switch, the default) or "pulse" (smooth fade).</summary>
    public string Style { get; init; } = BlinkSpec.Flash;

    /// <summary>Brightness kept in a flash's dim half (0.1–0.9).</summary>
    public double FlashStrength { get; init; } = 0.25;

    /// <summary>Brightness kept at the bottom of a pulse (0.1–0.9).</summary>
    public double PulseStrength { get; init; } = 0.6;

    /// <summary>One full flash cycle, normal → dim → normal (400–10000 ms). Wins over <see cref="IntervalMilliseconds"/>.</summary>
    public int? PeriodMilliseconds { get; init; }

    /// <summary>One full pulse cycle (400–10000 ms). Slower than a flash by default: a fade reads better slowly.</summary>
    public int PulsePeriodMilliseconds { get; init; } = 2000;

    /// <summary>
    /// Before #131: how long each half of a flash lasts. Still honoured when
    /// <see cref="PeriodMilliseconds"/> is not set (flash period = 2 × interval). Never affects pulse.
    /// </summary>
    public int? IntervalMilliseconds { get; init; }

    /// <summary>The default flash cycle length, clamped to the supported range.</summary>
    public int EffectivePeriodMilliseconds =>
        Math.Clamp(PeriodMilliseconds ?? (IntervalMilliseconds * 2) ?? 1000, BlinkSpec.MinPeriodMs, BlinkSpec.MaxPeriodMs);

    /// <summary>The default pulse cycle length, clamped to the supported range.</summary>
    public int EffectivePulsePeriodMilliseconds =>
        Math.Clamp(PulsePeriodMilliseconds, BlinkSpec.MinPeriodMs, BlinkSpec.MaxPeriodMs);
}
