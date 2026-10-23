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

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                lines.Enqueue($"{category} scope: {Describe(state)}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} | {Describe(state)} {exception}");

            // Structured values too: a key could hide in a property that the message template omits.
            private static string Describe<T>(T state) =>
                state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"))
                    : state?.ToString() ?? "";
        }
    }
}
