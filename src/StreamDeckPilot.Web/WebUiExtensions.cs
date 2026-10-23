using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using StreamDeckPilot.Web.Components;
using StreamDeckPilot.Web.Services;

namespace StreamDeckPilot.Web;

/// <summary>
/// Hooks the web UI into the API host. Both calls do nothing unless <c>Web:Enabled</c> is true, so a
/// default install serves exactly the REST API it always did.
/// </summary>
public static class WebUiExtensions
{
    public static bool IsWebUiEnabled(this IConfiguration configuration) =>
        configuration.GetSection(WebOptions.Section).GetValue<bool>(nameof(WebOptions.Enabled));

    public static WebApplicationBuilder AddWebUi(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<WebOptions>(builder.Configuration.GetSection(WebOptions.Section));
        if (!builder.Configuration.IsWebUiEnabled()) return builder;

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddMudServices();
        // Per circuit: each browser tab has its own live subscription, disposed with the circuit (#108).
        builder.Services.AddScoped<LiveState>();
        builder.Services.AddScoped<IDeviceDirectory, DeviceDirectory>();
        builder.Services.AddScoped<DeviceSelection>();
        return builder;
    }

    /// <summary>
    /// Maps the UI under <c>/</c>. Page routes must not collide with the REST API (<c>/devices…</c>,
    /// <c>/config…</c>, <c>/health</c>, <c>/openapi.json</c>); UI pages live at <c>/</c> and <c>/board/…</c> (not <c>/deck</c>: that is the example path base).
    /// Every UI endpoint carries <see cref="WebUiEndpointMetadata"/> so the API key middleware lets it through.
    /// </summary>
    public static WebApplication MapWebUi(this WebApplication app)
    {
        if (!app.Configuration.IsWebUiEnabled()) return app;

        app.UseAntiforgery();
        app.MapStaticAssets().Add(e => e.Metadata.Add(WebUiEndpointMetadata.Instance));
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .Add(e => e.Metadata.Add(WebUiEndpointMetadata.Instance));
        return app;
    }
}
