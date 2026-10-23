using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Api.Endpoints;
using StreamDeckPilot.Infrastructure.Events;

namespace StreamDeckPilot.Api.Events;

/// <summary>
/// <c>GET /api/events</c> (#108): Server-Sent Events. A <c>snapshot</c> event first, then one event
/// per change, pushed as it happens. Keep-alive comments flow on an idle stream. A client that
/// reconnects with <c>Last-Event-ID</c> gets the events it missed, or a fresh snapshot when they're
/// no longer buffered or the service restarted. See docs/live-events.md.
/// </summary>
public static class EventStreamEndpoints
{
    public const string Path = "/api/events";
    public const string SnapshotEvent = "snapshot";

    public static void MapEventStream(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, StreamAsync)
            .WithName("StreamEvents")
            .WithSummary("Live events (Server-Sent Events)")
            .WithDescription(
                "A `text/event-stream`. The first event is `snapshot` (devices with connection state, active page " +
                "and key content hashes; broker state; brightness; latest value per bound topic). Then one event per " +
                "change: `DeviceConnected`, `DeviceDisconnected`, `KeyRendered`, `ValueReceived`, `BrokerOnline`, " +
                "`BrokerOffline`, `ConfigChanged`, `ConfigRejected`, `PageChanged`, `BrightnessChanged`. Each has " +
                "`id: {bootId}:{sequence}`; send it back as `Last-Event-ID` to resume. `types` is a comma-separated " +
                "list of event names; `serial` limits per-device events (and the snapshot) to one deck. " +
                "Payloads are described in docs/live-events.md.")
            .WithTags("Live events")
            .Produces<string>(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task StreamAsync(HttpContext http, string? types, string? serial,
        IEventBus bus, LiveSnapshotService snapshots, IOptions<EventsOptions> options)
    {
        var filter = EventFilter.Parse(types, serial, out var error);
        if (error is not null)
        {
            await ApiProblem.BadRequest(error).ExecuteAsync(http);
            return;
        }

        var response = http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";   // nginx: don't buffer the stream
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var ct = http.RequestAborted;
        var heartbeat = TimeSpan.FromSeconds(Math.Max(1, options.Value.HeartbeatSeconds));

        // Subscribe before the snapshot or replay, so nothing in between is lost; anything already
        // covered is skipped by sequence below.
        using var subscription = bus.Subscribe(filter);
        try
        {
            await WriteAsync(response, "retry: 3000\n\n", ct);

            long last;
            var resumeFrom = LiveEventJson.ParseEventId(http.Request.Headers["Last-Event-ID"].FirstOrDefault(), bus.BootId);
            if (resumeFrom is { } from && bus.TryReplay(from, filter, out var missed, out var upTo))
            {
                foreach (var e in missed) await WriteEventAsync(response, bus.BootId, e, ct);
                last = upTo;
            }
            else
            {
                last = await WriteSnapshotAsync(response, bus.BootId, snapshots, filter.Serial, ct);
            }
            await response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                bool more;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(heartbeat);
                    try { more = await subscription.Reader.WaitToReadAsync(wait.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await WriteAsync(response, ": keep-alive\n\n", ct);
                        await response.Body.FlushAsync(ct);
                        continue;
                    }
                }
                if (!more) break;   // bus shut down

                if (subscription.TakeOverflow())
                    last = await WriteSnapshotAsync(response, bus.BootId, snapshots, filter.Serial, ct);   // fell behind: resync

                while (subscription.Reader.TryRead(out var e))
                {
                    if (e.Sequence <= last) continue;
                    await WriteEventAsync(response, bus.BootId, e, ct);
                    last = e.Sequence;
                }
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }   // client went away
        catch (IOException) when (ct.IsCancellationRequested) { }
    }

    private static async Task<long> WriteSnapshotAsync(HttpResponse response, string bootId,
        LiveSnapshotService snapshots, string? serial, CancellationToken ct)
    {
        var snapshot = await snapshots.GetAsync(serial);
        await WriteAsync(response,
            $"id: {LiveEventJson.EventId(bootId, snapshot.Sequence)}\nevent: {SnapshotEvent}\ndata: {LiveEventJson.Serialize(snapshot)}\n\n", ct);
        return snapshot.Sequence;
    }

    private static Task WriteEventAsync(HttpResponse response, string bootId, LiveEvent e, CancellationToken ct) =>
        WriteAsync(response,
            $"id: {LiveEventJson.EventId(bootId, e.Sequence)}\nevent: {e.Type}\ndata: {LiveEventJson.Serialize(e)}\n\n", ct);

    private static Task WriteAsync(HttpResponse response, string text, CancellationToken ct) =>
        response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();
}
