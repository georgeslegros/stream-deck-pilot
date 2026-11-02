using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StreamDeckPilot.Tests.Api;

/// <summary>
/// The API key never reaches the logs (#108 review): not from the hosting request log, the hub's
/// connection logs, the SSE stream or the API key middleware, with everything logged at Trace.
/// </summary>
public sealed class ApiKeyLoggingTests : IAsyncDisposable
{
    private const string WrongKey = "wrong-key-value-123";
    private readonly StreamDeckApiFactory _base = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ApiKeyLoggingTests()
    {
        // The app logs through Serilog; swap in a plain logger factory that sends everything,
        // from every category, to the capturing provider.
        _factory = _base.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l
                .SetMinimumLevel(LogLevel.Trace)
                .AddProvider(_logs)))));
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await ((IAsyncDisposable)_base).DisposeAsync();
    }

    [Fact]
    public async Task HubAndSse_WithTheKey_NeverLogIt()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", StreamDeckApiFactory.TestApiKey);

        await using (var sse = await SseClient.OpenAsync(client, "/api/events"))
            Assert.Equal("snapshot", (await sse.NextAsync()).Event);

        foreach (var bearer in new[] { false, true })
        {
            await using var hub = new HubConnectionBuilder()
                .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/live"), o =>
                {
                    o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                    if (bearer) o.AccessTokenProvider = () => Task.FromResult<string?>(StreamDeckApiFactory.TestApiKey);
                    else o.Headers["X-Api-Key"] = StreamDeckApiFactory.TestApiKey;
                })
                .Build();
            await hub.StartAsync();
            await hub.InvokeAsync<JsonElement>("Subscribe", (string[]?)null, (string?)null);
        }

        // A wrong key is refused, and isn't logged either.
        var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", WrongKey);
        await wrong.GetAsync("/devices");

        var lines = _logs.Lines;
        Assert.Contains(lines, l => l.Contains("Request starting"));   // the hosting log was captured
        Assert.Contains(lines, l => l.Contains("/hubs/live"));
        Assert.DoesNotContain(lines, l => l.Contains(StreamDeckApiFactory.TestApiKey));
        Assert.DoesNotContain(lines, l => l.Contains(WrongKey));
    }

    [Fact]
    public async Task NamedKeys_CreatedUsedAndRevoked_NeverReachTheLogs()
    {
        // #98: an sdp_ key is shown once and never logged, nor is its hash; only its name and id.
        _factory.CreateClient();   // starts the host
        var store = _factory.Services.GetRequiredService<StreamDeckPilot.Infrastructure.Security.ApiKeyStore>();
        var created = (await store.CreateAsync("logging test", StreamDeckPilot.Infrastructure.Security.ApiKeyScope.ReadOnly, null, "admin")).Created!;
        var key = created.Key;
        var secret = key[(StreamDeckPilot.Infrastructure.Security.ApiKeyFormat.Prefix.Length + StreamDeckPilot.Infrastructure.Security.ApiKeyFormat.IdLength + 1)..];

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        await client.GetAsync("/devices");
        await client.PostAsync("/devices/SN1/force-render", null);   // refused: read-only
        await using (var sse = await SseClient.OpenAsync(client, "/api/events"))
            Assert.Equal("snapshot", (await sse.NextAsync()).Event);
        await using (var hub = new HubConnectionBuilder()
                         .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/live"), o =>
                         {
                             o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                             o.Transports = HttpTransportType.LongPolling;
                             o.AccessTokenProvider = () => Task.FromResult<string?>(key);
                         })
                         .Build())
        {
            await hub.StartAsync();
            await hub.InvokeAsync<JsonElement>("Subscribe", (string[]?)null, (string?)null);
        }

        // A wrong named key is refused, and isn't logged either.
        var wrongNamed = key[..^4] + "AAAA";
        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Api-Key", wrongNamed);
        await anonymous.GetAsync("/devices");

        await store.RevokeAsync(created.Record.Id, "admin");
        await client.GetAsync("/devices");   // now 401

        var lines = _logs.Lines;
        Assert.Contains(lines, l => l.Contains("logging test") && l.Contains("created"));   // the audit line was captured
        Assert.Contains(lines, l => l.Contains("logging test") && l.Contains("revoked"));
        Assert.DoesNotContain(lines, l => l.Contains(secret));
        Assert.DoesNotContain(lines, l => l.Contains(created.Record.Hash));
        Assert.DoesNotContain(lines, l => l.Contains(wrongNamed[^30..]));
    }
}
