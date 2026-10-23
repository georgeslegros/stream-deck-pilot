using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// First-run setup (#97). While local accounts are on and none exists, the setup page creates the
/// first admin, but only with this one-time token. It is printed to the service log at startup, so
/// only someone who can read the logs (the host's owner) can claim the install; a stranger on the
/// LAN who opens the page first can't. A new token is made on every start until setup is done.
/// </summary>
public sealed class SetupToken
{
    private readonly object _gate = new();
    private string? _token;

    /// <summary>Makes a new token (replacing any earlier one) and returns it.</summary>
    public string Issue()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        lock (_gate) _token = token;
        return token;
    }

    public bool IsIssued { get { lock (_gate) return _token is not null; } }

    /// <summary>Constant-time check against the current token.</summary>
    public bool Matches(string? candidate)
    {
        string? token;
        lock (_gate) token = _token;
        if (token is null || string.IsNullOrEmpty(candidate)) return false;
        var a = Encoding.UTF8.GetBytes(token);
        var b = Encoding.UTF8.GetBytes(candidate.Trim());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Used once: after setup the token stops working.</summary>
    public void Consume() { lock (_gate) _token = null; }
}

/// <summary>Issues and logs the setup token at startup when the install has no account yet.</summary>
public sealed class SetupTokenAnnouncer(
    SetupToken token, LocalUserStore users, IOptions<AuthOptions> auth, IOptions<WebOptions> web,
    IConfiguration configuration, ILogger<SetupTokenAnnouncer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!auth.Value.LocalEnabled || await users.AnyAsync(cancellationToken)) return;

        var pathBase = configuration["Proxy:PathBase"]?.Trim() ?? "";
        var where = web.Value.NormalisedPublicOrigin is { } origin
            ? $"{origin}{pathBase}/account/setup"
            : $"{pathBase}/account/setup on the web UI";
        // Deliberately logged: this is how the owner proves they control the install. It only
        // works until the first admin exists, and a restart replaces it.
        logger.LogWarning(
            "No local account exists yet. Open {SetupUrl} and enter this one-time setup token to create the admin: {SetupToken}",
            where, token.Issue());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
