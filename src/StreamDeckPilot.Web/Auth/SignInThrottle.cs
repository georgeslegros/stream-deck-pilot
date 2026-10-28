using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// Rate limit for sign-in and setup attempts (#97): at most <see cref="AuthOptions.AttemptLimit"/>
/// form posts per client address per <see cref="AuthOptions.AttemptWindowSeconds"/>. The address is
/// the connection's after forwarded headers, which are only honoured from trusted proxies
/// (<c>Proxy:TrustedNetworks</c>/<c>TrustedProxies</c>, docs/reverse-proxy.md): an untrusted peer
/// can't pick a new address per attempt. Each purpose (sign-in, setup) has its own budget. Sign-in
/// also has a per-user-name budget (<see cref="AuthOptions.UserAttemptLimit"/>), so guesses spread
/// over many addresses still run out.
/// </summary>
public sealed class SignInThrottle(IOptions<AuthOptions> options, TimeProvider? time = null)
{
    private const int MaxTracked = 10_000;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string Purpose, string Address), Window> _windows = new();

    /// <summary>Counts an attempt. False when the address has used up its budget; <paramref name="retryAfter"/> says for how long.</summary>
    public bool TryAcquire(string purpose, IPAddress? address, out TimeSpan retryAfter) =>
        TryAcquire(purpose, address?.ToString() ?? "unknown", options.Value.AttemptLimit, out retryAfter);

    /// <summary>Counts a sign-in attempt for a user name (case-insensitive), whatever the address. Always true when the limit is 0 (off).</summary>
    public bool TryAcquireForUser(string userName, out TimeSpan retryAfter)
    {
        if (options.Value.UserAttemptLimit == 0)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }
        return TryAcquire("signin-user", userName.Trim().ToLowerInvariant(), options.Value.UserAttemptLimit, out retryAfter);
    }

    private bool TryAcquire(string purpose, string subject, int limit, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow();
        var length = TimeSpan.FromSeconds(options.Value.AttemptWindowSeconds);
        var key = (purpose, subject);

        if (_windows.Count > MaxTracked) Prune(now, length);

        var window = _windows.AddOrUpdate(key,
            _ => new Window(now, 1),
            (_, w) => now - w.Start >= length ? new Window(now, 1) : w with { Count = w.Count + 1 });

        if (window.Count <= limit)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }
        retryAfter = window.Start + length - now;
        return false;
    }

    private void Prune(DateTimeOffset now, TimeSpan length)
    {
        foreach (var (key, w) in _windows)
            if (now - w.Start >= length) _windows.TryRemove(key, out _);
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}
