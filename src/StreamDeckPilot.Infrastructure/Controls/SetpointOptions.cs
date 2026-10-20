namespace StreamDeckPilot.Infrastructure.Controls;

/// <summary>Setpoint controls (#132), bound from the <c>Setpoint</c> configuration section.</summary>
public record SetpointOptions
{
    /// <summary>Band behind the target on the value key while a new target is not yet confirmed (#RRGGBB).</summary>
    public string PendingColour { get; init; } = "#FFB300";

    /// <summary>Debounce after the last +/− press before the target is published, when the control sets no <c>commit.delayMs</c>.</summary>
    public int DefaultCommitDelayMs { get; init; } = 1500;

    /// <summary>How long a published target waits for the target topic to report it, when the control sets no <c>confirmTimeoutSeconds</c>.</summary>
    public double DefaultConfirmTimeoutSeconds { get; init; } = 30;

    /// <summary>How long the value key shows the failed look after an unconfirmed target.</summary>
    public int FailedFlashMilliseconds { get; init; } = 600;

    /// <summary>Null when the options are usable; otherwise why not (checked at startup).</summary>
    public string? Error()
    {
        if (!(PendingColour is { Length: 7 } c && c[0] == '#' && c.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0))
            return $"Setpoint:PendingColour must be #RRGGBB (got '{PendingColour}').";
        if (DefaultCommitDelayMs is < 0 or > Core.Models.Config.SetpointCommit.MaxDelayMs)
            return $"Setpoint:DefaultCommitDelayMs must be between 0 and {Core.Models.Config.SetpointCommit.MaxDelayMs}.";
        if (!(DefaultConfirmTimeoutSeconds >= Core.Models.Config.SetpointControl.MinConfirmTimeoutSeconds
              && DefaultConfirmTimeoutSeconds <= Core.Models.Config.SetpointControl.MaxConfirmTimeoutSeconds))
            return $"Setpoint:DefaultConfirmTimeoutSeconds must be between {Core.Models.Config.SetpointControl.MinConfirmTimeoutSeconds} and {Core.Models.Config.SetpointControl.MaxConfirmTimeoutSeconds}.";
        if (FailedFlashMilliseconds is < 0 or > 10_000)
            return "Setpoint:FailedFlashMilliseconds must be between 0 and 10000.";
        return null;
    }
}
