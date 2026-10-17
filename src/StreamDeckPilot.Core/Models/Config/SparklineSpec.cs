namespace StreamDeckPilot.Core.Models.Config;

// Opt-in trend line drawn behind the value (#25): the last Hours of the button's numeric values,
// kept in memory only (history starts empty after a restart).
public record SparklineSpec(double Hours)
{
    public const double MaxHours = 48;
}
