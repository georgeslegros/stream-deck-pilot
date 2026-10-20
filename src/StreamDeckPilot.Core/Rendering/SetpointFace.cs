namespace StreamDeckPilot.Core.Rendering;

/// <summary>Which of a setpoint's three keys (#132) a render state draws.</summary>
public enum SetpointKeyRole
{
    Minus,
    Value,
    Plus,
}

/// <summary>
/// What a setpoint key (#132) shows beyond a normal tile. On the value key: <see cref="Current"/>
/// large, <see cref="Target"/> small underneath (on a band in <see cref="PendingColour"/> while
/// <see cref="Pending"/>), and the optional <see cref="Label"/> on top. On the +/− keys:
/// <see cref="Disabled"/> at the end of the range.
/// </summary>
public sealed record SetpointFace(
    SetpointKeyRole Role,
    string? Current = null,
    string? Target = null,
    string? Label = null,
    bool Pending = false,
    string? PendingColour = null,
    bool Disabled = false);
