using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace StreamDeckPilot.Tests;

/// <summary>Captures every log line, with its structured values and scopes, for tests that check what is (not) logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
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
