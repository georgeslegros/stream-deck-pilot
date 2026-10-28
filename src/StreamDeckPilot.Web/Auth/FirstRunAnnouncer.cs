using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Security;

namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// First-run setup (#97). While local accounts are on and none exists, the setup page creates the
/// first admin, but only for someone who enters the bootstrap API key (the <c>ApiKey</c> setting):
/// a stranger on the LAN who opens the page first can't claim the install. This says so in the log
/// at startup (never the key itself), or says why setup can't run.
/// </summary>
public sealed class FirstRunAnnouncer(
    LocalUserStore users, IApiKeyValidator keys, IOptions<AuthOptions> auth, IOptions<WebOptions> web,
    IConfiguration configuration, ILogger<FirstRunAnnouncer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!auth.Value.LocalEnabled || await users.StateAsync(cancellationToken) != LocalUsersState.Empty) return;

        var pathBase = configuration["Proxy:PathBase"]?.Trim() ?? "";
        var where = web.Value.NormalisedPublicOrigin is { } origin
            ? $"{origin}{pathBase}/account/setup"
            : $"{pathBase}/account/setup on the web UI";

        if (keys.BootstrapKeyAvailable)
            logger.LogWarning("No local account exists yet. Open {SetupUrl} and enter the API key (the ApiKey setting) to create the admin.", where);
        else
            logger.LogError("No local account exists yet, and first-run setup needs the bootstrap API key, which isn't set. Set ApiKey and restart, or configure an OpenID Connect provider (docs/auth.md).");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
