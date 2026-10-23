using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using StreamDeckPilot.Infrastructure.Events;

namespace StreamDeckPilot.Api.Events;

/// <summary>
/// SignalR hub for external live clients (#108), at <c>/hubs/live</c>. A client calls
/// <see cref="Subscribe"/> with the event types and deck it wants; it gets a snapshot back and
/// then receives <c>event</c> messages (the same payloads as SSE). <c>resync</c> means it fell
/// behind: call <see cref="GetSnapshot"/> and continue. After a reconnect, call
/// <see cref="Subscribe"/> again. Authenticated with the API key as the <c>X-Api-Key</c> header or a
/// Bearer token, never a query parameter; see docs/live-events.md.
/// </summary>
public sealed class LiveHub(LiveSnapshotService snapshots) : Hub
{
    public const string Path = "/hubs/live";
    public const string EventMethod = "event";
    public const string ResyncMethod = "resync";
    private const string GroupsKey = "groups";

    /// <summary>
    /// Replaces this connection's subscription. <paramref name="types"/>: event names (null or
    /// empty = all); <paramref name="serial"/>: one deck for per-device events (null = all decks).
    /// Returns the snapshot to apply the following events to.
    /// </summary>
    public async Task<JsonElement> Subscribe(string[]? types, string? serial)
    {
        var filter = EventFilter.Parse(types is null ? null : string.Join(',', types), serial, out var error);
        if (error is not null) throw new HubException(error);

        if (Context.Items.TryGetValue(GroupsKey, out var previous) && previous is string[] old)
            foreach (var group in old)
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);

        var groups = GroupsFor(filter);
        foreach (var group in groups)
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        Context.Items[GroupsKey] = groups;

        return await GetSnapshot(filter.Serial);
    }

    /// <summary>The current state, optionally for one deck.</summary>
    public async Task<JsonElement> GetSnapshot(string? serial) =>
        JsonSerializer.SerializeToElement(await snapshots.GetAsync(string.IsNullOrWhiteSpace(serial) ? null : serial),
            LiveEventJson.Options);

    /// <summary>One group per event type, narrowed to a deck for per-device types when a serial is given.</summary>
    internal static string[] GroupsFor(EventFilter filter) =>
        [.. (filter.Types ?? LiveEventTypes.All)
            .Select(type => Group(type, LiveEventTypes.PerDevice[type] ? filter.Serial : null))];

    internal static string Group(string type, string? serial) =>
        serial is null ? $"type:{type}" : $"type:{type}:serial:{serial}";
}

/// <summary>Forwards every bus event to the hub groups that asked for it.</summary>
public sealed class LiveHubBroadcaster(IEventBus bus, IHubContext<LiveHub> hub, ILogger<LiveHubBroadcaster> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = bus.Subscribe();
        try
        {
            await foreach (var e in subscription.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (subscription.TakeOverflow())
                    {
                        logger.LogWarning("Live hub fell behind and dropped events; asking clients to resync");
                        await hub.Clients.All.SendAsync(LiveHub.ResyncMethod, stoppingToken);
                    }

                    var payload = LiveEventJson.ToElement(e);
                    List<string> groups = [LiveHub.Group(e.Type, null)];
                    if (e.Serial is not null && LiveEventTypes.PerDevice.GetValueOrDefault(e.Type))
                        groups.Add(LiveHub.Group(e.Type, e.Serial));
                    await hub.Clients.Groups(groups).SendAsync(LiveHub.EventMethod, payload, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not forward live event {Type} to the hub", e.Type);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
