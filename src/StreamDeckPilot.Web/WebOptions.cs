namespace StreamDeckPilot.Web;

/// <summary>
/// The web UI (#95). Off by default: until sign-in (#97) lands, anyone who can reach the UI can see
/// the decks, so it must only be turned on where the network is private (LAN/VPN). The REST API and
/// its key are unaffected either way.
/// </summary>
public sealed class WebOptions
{
    public const string Section = "Web";

    /// <summary><c>Web__Enabled=true</c> serves the UI under <c>/</c>. Default false: nothing UI-related is mapped.</summary>
    public bool Enabled { get; set; }
}
