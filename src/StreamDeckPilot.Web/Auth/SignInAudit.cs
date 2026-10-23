using System.Net;
using Microsoft.Extensions.Logging;
using StreamDeckPilot.Infrastructure.Events;

namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// The sign-in audit log (#97): every sign-in, failure, setup and sign-out, in the service log under
/// the <c>StreamDeckPilot.Audit</c> category. Never logs a password or a token. Failures don't log
/// the user name either (people type passwords into it by mistake) and are also published as
/// <see cref="SignInFailed"/> on the live event bus.
/// </summary>
public sealed class SignInAudit(ILoggerFactory loggers, IEventBus? bus = null)
{
    private readonly ILogger _log = loggers.CreateLogger("StreamDeckPilot.Audit");

    public void Succeeded(string method, string user, string role, IPAddress? address) =>
        _log.LogInformation("Sign-in: {User} signed in with {Method} as {Role} from {Address}", user, method, role, address?.ToString());

    /// <param name="detail">For provider failures: the handler's error message (IdentityModel hides personal data in it).</param>
    public void Failed(string method, string reason, IPAddress? address, string? detail = null)
    {
        if (detail is null)
            _log.LogWarning("Sign-in refused: {Method} attempt from {Address} ({Reason})", method, address?.ToString(), reason);
        else
            _log.LogWarning("Sign-in refused: {Method} attempt from {Address} ({Reason}): {Detail}", method, address?.ToString(), reason, detail);
        bus?.Publish(new SignInFailed(method, reason));
    }

    public void SetupCompleted(string user, IPAddress? address) =>
        _log.LogWarning("First-run setup: admin account {User} created from {Address}", user, address?.ToString());

    public void SignedOut(string? user, IPAddress? address) =>
        _log.LogInformation("Sign-out: {User} from {Address}", user, address?.ToString());

    public void PlainHttp(string user) =>
        _log.LogWarning("Sign-in: {User} signed in over plain HTTP, so the session cookie isn't marked Secure. Serve the UI over HTTPS (docs/reverse-proxy.md).", user);
}
