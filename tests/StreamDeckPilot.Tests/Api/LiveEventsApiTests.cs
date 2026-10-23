using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Tests.Api;

/// <summary>Live events over HTTP (#108): the SSE stream at /api/events and the SignalR hub at /hubs/live.</summary>
public sealed class LiveEventsApiTests : IAsyncDisposable
{
    private readonly StreamDeckApiFactory _factory = new();

    public async ValueTask DisposeAsync() => await ((IAsyncDisposable)_factory).DisposeAsync();

    private static StringContent ConfigBody(DeviceConfig config) =>
        new(JsonSerializer.Serialize(config, JsonOptions.Default), Encoding.UTF8, "application/json");

    private static StringContent ValidConfig(string serial) => ConfigBody(new DeviceConfig(2, serial, [new ButtonGridPage("main", [])]));

    private static StringContent InvalidConfig(string serial) => ConfigBody(new DeviceConfig(2, serial, [
        new ButtonGridPage("main", [new ButtonDefinition("b", 99, "main", new DisplaySpec(), null, [], new Dictionary<string, IReadOnlyList<ButtonAction>>())]),
    ]));

    // ── SSE ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sse_StartsWithASnapshot_ThenPushesChanges()
    {
        await _factory.SeedDeviceAsync("SN001");
        var client = _factory.CreateAuthenticatedClient();
        await using var sse = await SseClient.OpenAsync(client, "/api/events");

        Assert.Equal("text/event-stream", sse.Response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", sse.Response.Headers.CacheControl?.ToString());
        Assert.Equal("no", sse.Response.Headers.GetValues("X-Accel-Buffering").Single());

        var snapshot = await sse.NextAsync();
        Assert.Equal("snapshot", snapshot.Event);
        var device = snapshot.Json.GetProperty("devices").EnumerateArray().Single();
        Assert.Equal("SN001", device.GetProperty("serial").GetString());
        Assert.Equal("unknown", device.GetProperty("state").GetString());
        Assert.True(snapshot.Json.TryGetProperty("broker", out _));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/devices/SN001/config", InvalidConfig("SN001"))).StatusCode);
        var rejected = await sse.NextAsync("ConfigRejected");
        Assert.Equal("SN001", rejected.Json.GetProperty("serial").GetString());
        Assert.Contains("out of range", rejected.Json.GetProperty("errors")[0].GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/devices/SN001/config", ValidConfig("SN001"))).StatusCode);
        var changed = await sse.NextAsync("ConfigChanged");
        Assert.Equal("ConfigChanged", changed.Json.GetProperty("type").GetString());
        Assert.StartsWith(snapshot.Id!.Split(':')[0] + ":", changed.Id);
    }

    [Fact]
    public async Task Sse_RequiresTheApiKey()
    {
        var response = await _factory.CreateClient().GetAsync("/api/events");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sse_UnknownEventType_Is400()
    {
        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/events?types=ConfigChanged,Nope");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Nope", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sse_FiltersByTypeAndDeck()
    {
        await _factory.SeedDeviceAsync("SN001");
        await _factory.SeedDeviceAsync("SN002");
        var client = _factory.CreateAuthenticatedClient();
        await using var sse = await SseClient.OpenAsync(client, "/api/events?types=ConfigChanged&serial=SN002");

        var snapshot = await sse.NextAsync();
        Assert.Equal("SN002", snapshot.Json.GetProperty("devices").EnumerateArray().Single().GetProperty("serial").GetString());

        await client.PutAsync("/devices/SN001/config", ValidConfig("SN001"));     // other deck
        await client.PutAsync("/devices/SN002/config", InvalidConfig("SN002"));   // other type
        await client.PutAsync("/devices/SN002/config", ValidConfig("SN002"));

        var next = await sse.NextAsync();
        Assert.Equal("ConfigChanged", next.Event);
        Assert.Equal("SN002", next.Json.GetProperty("serial").GetString());
    }

    [Fact]
    public async Task Sse_ResumesFromLastEventId_OrStartsOverFromASnapshot()
    {
        await _factory.SeedDeviceAsync("SN001");
        var client = _factory.CreateAuthenticatedClient();

        string lastId;
        await using (var first = await SseClient.OpenAsync(client, "/api/events?types=ConfigChanged"))
        {
            await first.NextAsync("snapshot");
            await client.PutAsync("/devices/SN001/config", ValidConfig("SN001"));
            lastId = (await first.NextAsync("ConfigChanged")).Id!;
        }

        // Missed while disconnected, then resumed: the missed event comes first, no snapshot.
        await client.PutAsync("/devices/SN001/config", ValidConfig("SN001"));
        await using (var resumed = await SseClient.OpenAsync(client, "/api/events?types=ConfigChanged", lastId))
        {
            var missed = await resumed.NextAsync();
            Assert.Equal("ConfigChanged", missed.Event);
            Assert.True(long.Parse(missed.Id!.Split(':')[1]) > long.Parse(lastId.Split(':')[1]));
        }

        // A resume point from another run of the service: start over.
        await using var fresh = await SseClient.OpenAsync(client, "/api/events", "someotherboot:5");
        Assert.Equal("snapshot", (await fresh.NextAsync()).Event);
    }

    [Fact]
    public async Task OpenApi_DescribesTheStream()
    {
        var doc = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/openapi.json");
        var get = doc.GetProperty("paths").GetProperty("/api/events").GetProperty("get");
        Assert.Equal("StreamEvents", get.GetProperty("operationId").GetString());
        Assert.True(get.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("text/event-stream", out _));
    }

    // ── SignalR hub ─────────────────────────────────────────────────────────

    private HubConnection Hub(Action<HttpConnectionOptions>? configure = null)
    {
        _factory.CreateClient();   // starts the test server
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/live"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;   // the test server has no WebSockets
                configure?.Invoke(o);
            })
            .Build();
    }

    [Fact]
    public async Task Hub_Subscribe_ReturnsASnapshot_ThenPushesMatchingEvents()
    {
        await _factory.SeedDeviceAsync("SN001");
        await _factory.SeedDeviceAsync("SN002");
        await using var hub = Hub(o => o.Headers["X-Api-Key"] = StreamDeckApiFactory.TestApiKey);
        var events = Channel.CreateUnbounded<JsonElement>();
        hub.On<JsonElement>("event", e => events.Writer.TryWrite(e));
        await hub.StartAsync();

        var snapshot = await hub.InvokeAsync<JsonElement>("Subscribe", new[] { "ConfigRejected" }, "SN001");
        Assert.Equal("SN001", snapshot.GetProperty("devices").EnumerateArray().Single().GetProperty("serial").GetString());

        var client = _factory.CreateAuthenticatedClient();
        await client.PutAsync("/devices/SN002/config", InvalidConfig("SN002"));   // other deck
        await client.PutAsync("/devices/SN001/config", ValidConfig("SN001"));     // other type
        await client.PutAsync("/devices/SN001/config", InvalidConfig("SN001"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var e = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal("ConfigRejected", e.GetProperty("type").GetString());
        Assert.Equal("SN001", e.GetProperty("serial").GetString());
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Hub_TakesTheKeyAsABearerToken()
    {
        await using var hub = Hub(o => o.AccessTokenProvider = () => Task.FromResult<string?>(StreamDeckApiFactory.TestApiKey));
        await hub.StartAsync();
        var snapshot = await hub.InvokeAsync<JsonElement>("GetSnapshot", (string?)null);
        Assert.True(snapshot.TryGetProperty("bootId", out _));
    }

    [Theory]
    [InlineData("/hubs/live/negotiate?negotiateVersion=1&access_token=" + StreamDeckApiFactory.TestApiKey)]
    [InlineData("/hubs/live?id=x&access_token=" + StreamDeckApiFactory.TestApiKey)]
    [InlineData("/api/events?access_token=" + StreamDeckApiFactory.TestApiKey)]
    [InlineData("/devices?access_token=" + StreamDeckApiFactory.TestApiKey)]
    public async Task TheKeyInTheQueryString_IsNeverAccepted(string url)
    {
        // URLs land in access logs: the key must only ever travel in a header.
        var client = _factory.CreateClient();
        var response = url.Contains("negotiate")
            ? await client.PostAsync(url, null)
            : await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hub_WithoutTheKey_IsRefused()
    {
        await using var hub = Hub();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Hub_UnknownEventType_IsAnError()
    {
        await using var hub = Hub(o => o.Headers["X-Api-Key"] = StreamDeckApiFactory.TestApiKey);
        await hub.StartAsync();
        var ex = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<JsonElement>("Subscribe", new[] { "Nope" }, (string?)null));
        Assert.Contains("Unknown event type 'Nope'", ex.Message);
    }
}
