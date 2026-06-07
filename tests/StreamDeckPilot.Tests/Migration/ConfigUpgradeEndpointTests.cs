using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Tests.Api;

namespace StreamDeckPilot.Tests.Migration;

public sealed class ConfigUpgradeEndpointTests : IAsyncDisposable
{
    private readonly StreamDeckApiFactory _factory = new();
    async ValueTask IAsyncDisposable.DisposeAsync() => await ((IAsyncDisposable)_factory).DisposeAsync();

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value, JsonOptions.Default), Encoding.UTF8, "application/json");

    [Fact]
    public async Task UpgradeEndpoint_V1Config_MigratesToCurrentVersion()
    {
        var config = new DeviceConfig(1, "SN1", [new ButtonGridPage("main", [])]);
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/config/upgrade", JsonBody(config));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SN1", result.GetProperty("serial").GetString());
        Assert.Equal(2, result.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task UpgradeEndpoint_BelowFloor_Returns422()
    {
        var client = _factory.CreateAuthenticatedClient();
        // Manually set schemaVersion to 0 (below floor)
        var body = """{"schemaVersion":0,"serial":"SN1","pages":[]}""";

        var response = await client.PostAsync("/config/upgrade",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("unsupported_schema_version", json);
    }

    [Fact]
    public async Task UpgradeEndpoint_InvalidJson_Returns400()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.PostAsync("/config/upgrade",
            new StringContent("not-json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
