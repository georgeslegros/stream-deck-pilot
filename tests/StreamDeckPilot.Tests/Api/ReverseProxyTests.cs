using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using StreamDeckPilot.Api.Proxy;

namespace StreamDeckPilot.Tests.Api;

/// <summary>
/// Reverse-proxy support (#109): forwarded headers only from trusted peers, the optional path
/// base, and health without a key. Forwarded headers are tested on a minimal host with the same
/// extension methods, so the probe can report what the app saw.
/// </summary>
public sealed class ReverseProxyTests
{
    private static async Task<WebApplication> ProbeHostAsync(Dictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.AddReverseProxySupport();
        var app = builder.Build();
        app.UseReverseProxySupport();
        app.MapGet("/probe", (HttpContext c) =>
            $"{c.Request.Scheme}|{c.Request.Host}|{c.Connection.RemoteIpAddress}|{c.Request.PathBase}");
        await app.StartAsync();
        return app;
    }

    private static async Task<string> ProbeAsync(WebApplication app, string remoteIp, string path = "/probe",
        string? forwardedFor = "203.0.113.7", string? proto = "https", string? host = "deck.example.com",
        string rawHost = "app:8080")
    {
        var context = await app.GetTestServer().SendAsync(c =>
        {
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            c.Request.Method = "GET";
            c.Request.Scheme = "http";
            c.Request.Host = new HostString(rawHost);
            c.Request.Path = path;
            if (forwardedFor is not null) c.Request.Headers["X-Forwarded-For"] = forwardedFor;
            if (proto is not null) c.Request.Headers["X-Forwarded-Proto"] = proto;
            if (host is not null) c.Request.Headers["X-Forwarded-Host"] = host;
        });
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    [Theory]
    [InlineData("172.18.0.2")]   // a Docker network
    [InlineData("192.168.1.10")]
    [InlineData("10.1.2.3")]
    [InlineData("127.0.0.1")]
    public async Task ForwardedHeaders_FromATrustedNetwork_AreHonoured(string proxyIp)
    {
        await using var app = await ProbeHostAsync();
        Assert.Equal("https|deck.example.com|203.0.113.7|", await ProbeAsync(app, proxyIp));
    }

    [Fact]
    public async Task ForwardedHeaders_FromAnUntrustedPeer_AreIgnored()
    {
        await using var app = await ProbeHostAsync();
        Assert.Equal("http|app:8080|198.51.100.20|", await ProbeAsync(app, "198.51.100.20"));
    }

    [Fact]
    public async Task ConfiguredNetworks_ReplaceTheDefaults()
    {
        await using var app = await ProbeHostAsync(new() { ["Proxy:TrustedNetworks:0"] = "198.51.100.0/24" });
        Assert.Equal("https|deck.example.com|203.0.113.7|", await ProbeAsync(app, "198.51.100.20"));
        Assert.Equal("http|app:8080|192.168.1.10|", await ProbeAsync(app, "192.168.1.10"));
    }

    [Fact]
    public async Task TrustedProxy_IsHonoured_OutsideTheNetworks()
    {
        await using var app = await ProbeHostAsync(new() { ["Proxy:TrustedProxies:0"] = "198.51.100.20" });
        Assert.Equal("https|deck.example.com|203.0.113.7|", await ProbeAsync(app, "198.51.100.20"));
    }

    [Fact]
    public async Task ForwardedHost_NotInAllowedHosts_IsIgnored()
    {
        // Host filtering checks the Host header the proxy passes on (Traefik keeps the client's by
        // default); a forwarded host outside AllowedHosts must not replace it.
        await using var app = await ProbeHostAsync(new() { ["AllowedHosts"] = "deck.example.com" });
        Assert.Equal("https|deck.example.com|203.0.113.7|", await ProbeAsync(app, "172.18.0.2", rawHost: "deck.example.com"));
        Assert.Equal("https|deck.example.com|203.0.113.7|",
            await ProbeAsync(app, "172.18.0.2", host: "evil.example.net", rawHost: "deck.example.com"));
    }

    [Fact]
    public async Task ForwardLimit_UnwindsOnlyOneHopByDefault()
    {
        await using var app = await ProbeHostAsync();
        // The client could prepend anything; only the entry the trusted proxy added is used.
        Assert.Equal("https|deck.example.com|203.0.113.7|",
            await ProbeAsync(app, "172.18.0.2", forwardedFor: "10.9.9.9, 203.0.113.7"));
    }

    [Fact]
    public async Task PathBase_IsStrippedAndExposed()
    {
        await using var app = await ProbeHostAsync(new() { ["Proxy:PathBase"] = "/deck" });
        Assert.Equal("https|deck.example.com|203.0.113.7|/deck", await ProbeAsync(app, "172.18.0.2", "/deck/probe"));
    }

    [Theory]
    [InlineData("deck")]
    [InlineData("/deck/")]
    [InlineData("/")]
    [InlineData("/a//b")]
    [InlineData("/deck?x=1")]
    public void InvalidPathBase_IsRejected(string pathBase) =>
        Assert.Contains("Proxy:PathBase", new ProxyOptions { PathBase = pathBase }.Error());

    [Fact]
    public void InvalidNetworkOrProxy_IsRejected()
    {
        Assert.Contains("TrustedNetworks", new ProxyOptions { TrustedNetworks = ["10.0.0.0"] }.Error());
        Assert.Contains("TrustedProxies", new ProxyOptions { TrustedProxies = ["proxy.local"] }.Error());
        Assert.Contains("ForwardLimit", new ProxyOptions { ForwardLimit = 0 }.Error());
        Assert.Null(new ProxyOptions { PathBase = "/stream/deck" }.Error());
    }

    [Fact]
    public async Task InvalidOptions_FailAtStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Proxy:PathBase"] = "deck" });
        builder.AddReverseProxySupport();
        await using var app = builder.Build();
        var error = Assert.Throws<InvalidOperationException>(() => app.UseReverseProxySupport());
        Assert.Contains("Proxy:PathBase 'deck'", error.Message);
    }

    // ── The real app under a path base ─────────────────────────────────────────

    [Fact]
    public async Task RealApp_UnderAPathBase_ServesApiHealthAndOpenApi()
    {
        await using var factory = new StreamDeckApiFactory(new() { ["Proxy:PathBase"] = "/deck" });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/deck/health")).StatusCode);
        var openApi = await client.GetAsync("/deck/openapi.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        // Generated links carry the base: the document's server URL ends with it.
        using (var doc = System.Text.Json.JsonDocument.Parse(await openApi.Content.ReadAsStringAsync()))
            Assert.EndsWith("/deck", doc.RootElement.GetProperty("servers")[0].GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/deck/devices")).StatusCode);

        var authed = factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/deck/devices")).StatusCode);
    }

    [Fact]
    public async Task RealApp_UnderAPathBase_404sOutsideIt_ExceptHealth()
    {
        await using var factory = new StreamDeckApiFactory(new() { ["Proxy:PathBase"] = "/deck" });
        var authed = factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.NotFound, (await authed.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await authed.GetAsync("/openapi.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await authed.GetAsync("/decks/devices")).StatusCode);
        // Container health checks talk to the app directly, without the base.
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task RealApp_WithoutAPathBase_HealthNeedsNoKey()
    {
        await using var factory = new StreamDeckApiFactory();
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/devices")).StatusCode);
    }
}
