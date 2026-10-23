namespace StreamDeckPilot.Web;

/// <summary>
/// Endpoint metadata on everything the UI maps (pages, the Blazor circuit hub, static assets). The
/// API key middleware lets these through: a browser has no X-Api-Key header, and the UI talks to the
/// app over its own circuit, not through the REST API. Everything else still needs the key.
/// </summary>
public sealed class WebUiEndpointMetadata
{
    public static readonly WebUiEndpointMetadata Instance = new();

    private WebUiEndpointMetadata() { }
}
