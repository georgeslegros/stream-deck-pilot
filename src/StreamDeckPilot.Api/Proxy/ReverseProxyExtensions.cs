using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace StreamDeckPilot.Api.Proxy;

/// <summary>
/// Reverse-proxy support (#109): forwarded headers from trusted peers, an optional path base,
/// and WebSockets. <see cref="UseReverseProxySupport"/> must be the first middleware.
/// </summary>
public static class ReverseProxyExtensions
{
    public static WebApplicationBuilder AddReverseProxySupport(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<ProxyOptions>()
            .Bind(builder.Configuration.GetSection(ProxyOptions.Section));

        // Built from ProxyOptions so the configuration is the only authority: the framework's
        // default (loopback only) is cleared, then the configured ranges and proxies are added.
        builder.Services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ProxyOptions>, IConfiguration>((o, proxy, config) =>
            {
                var p = proxy.Value;
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                o.ForwardLimit = p.ForwardLimit;
                o.KnownIPNetworks.Clear();
                o.KnownProxies.Clear();
                foreach (var network in p.EffectiveTrustedNetworks)
                    if (System.Net.IPNetwork.TryParse(network, out var parsed))
                        o.KnownIPNetworks.Add(parsed);
                foreach (var address in p.TrustedProxies ?? [])
                    if (IPAddress.TryParse(address, out var parsed))
                        o.KnownProxies.Add(parsed);

                // A forwarded host is only applied when AllowedHosts allows it ("*" = any).
                var allowed = (config["AllowedHosts"] ?? "*")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!allowed.Contains("*"))
                    foreach (var host in allowed)
                        o.AllowedHosts.Add(host);
            });
        return builder;
    }

    public static WebApplication UseReverseProxySupport(this WebApplication app)
    {
        // Checked here rather than with ValidateOnStart so a bad value fails before the first request
        // with the offending entry in the message, also in hosts that don't start hosted services.
        var proxy = app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;
        if (proxy.Error() is { } error)
            throw new InvalidOperationException(error);

        app.UseForwardedHeaders();

        if (proxy.NormalisedPathBase is { } pathBase)
        {
            app.UsePathBase(pathBase);
            // Outside the base only /health answers (container and orchestrator health checks that
            // bypass the proxy); everything else is a plain 404, so the app has a single address.
            app.Use((context, next) =>
            {
                if (context.Request.PathBase.HasValue || context.Request.Path.StartsWithSegments("/health"))
                    return next(context);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
            // Minimal hosting adds routing at the very start of the pipeline unless it's placed
            // explicitly; it has to run after UsePathBase to match the stripped path.
            app.UseRouting();
        }

        // SignalR (the web UI and the live hub) upgrades to WebSockets through the proxy.
        app.UseWebSockets();
        return app;
    }
}
