namespace StreamDeckPilot.Infrastructure.Navigation;

public record NavigationOptions
{
    /// <summary>
    /// After this many seconds without a key press or page change, a deck returns to its home page
    /// (the first page of its config). <c>0</c> turns it off. A device config can override it with
    /// <c>homeAfterInactivitySeconds</c>.
    /// </summary>
    public int HomeAfterInactivitySeconds { get; init; }
}
