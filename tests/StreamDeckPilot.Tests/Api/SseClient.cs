using System.Text;
using System.Text.Json;

namespace StreamDeckPilot.Tests.Api;

/// <summary>One Server-Sent Event as received.</summary>
public sealed record SseEvent(string? Id, string Event, string Data)
{
    public JsonElement Json => JsonDocument.Parse(Data).RootElement;
}

/// <summary>A minimal SSE reader over a streaming response (comments and retry lines skipped).</summary>
public sealed class SseClient : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private SseClient(HttpResponseMessage response, Stream stream)
    {
        _response = response;
        _reader = new StreamReader(stream, Encoding.UTF8);
    }

    public HttpResponseMessage Response => _response;

    public static async Task<SseClient> OpenAsync(HttpClient client, string url, string? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return new SseClient(response, await response.Content.ReadAsStreamAsync());
    }

    /// <summary>The next event, or a timeout.</summary>
    public async Task<SseEvent> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        string? id = null, name = null;
        var data = new StringBuilder();
        while (true)
        {
            var line = await _reader.ReadLineAsync(cts.Token)
                       ?? throw new EndOfStreamException("The event stream ended.");
            if (line.Length == 0)
            {
                if (name is null && data.Length == 0) continue;   // end of a comment or retry block
                return new SseEvent(id, name ?? "message", data.ToString());
            }
            if (line.StartsWith(':') || line.StartsWith("retry:")) continue;
            if (line.StartsWith("id: ")) id = line[4..];
            else if (line.StartsWith("event: ")) name = line[7..];
            else if (line.StartsWith("data: ")) data.Append(line[6..]);
        }
    }

    /// <summary>Skips events until one named <paramref name="name"/> (optionally also matching <paramref name="where"/>).</summary>
    public async Task<SseEvent> NextAsync(string name, Func<SseEvent, bool>? where = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"No '{name}' event in time.");
            var e = await NextAsync(left);
            if (e.Event == name && (where is null || where(e))) return e;
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}
