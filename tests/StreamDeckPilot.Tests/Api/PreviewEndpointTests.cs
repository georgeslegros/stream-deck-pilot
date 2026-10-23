using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Tests.Api;

/// <summary>The live preview endpoints (#27): PNGs, 404s, page and scale parameters, ETag/304.</summary>
public sealed class PreviewEndpointTests : IAsyncDisposable
{
    private const string Serial = "SN_PREV";
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private readonly StreamDeckApiFactory _factory = new();

    async ValueTask IAsyncDisposable.DisposeAsync() =>
        await ((IAsyncDisposable)_factory).DisposeAsync();

    private async Task<HttpClient> SeedAsync()
    {
        await _factory.SeedDeviceAsync(Serial);
        var client = _factory.CreateAuthenticatedClient();
        var goToOther = new Dictionary<string, IReadOnlyList<ButtonAction>>
        {
            ["Tap"] = [new NavigateAction("other")],
        };
        var config = new DeviceConfig(2, Serial, [
            new ButtonGridPage("main", [new ButtonDefinition("go", 0, "main",
                new DisplaySpec(Center: new TextZone("Go", null)), null, [], goToOther)]),
            new ButtonGridPage("other", [new ButtonDefinition("back", 4, "other",
                new DisplaySpec(Center: new TextZone("Back", null)), null, [],
                new Dictionary<string, IReadOnlyList<ButtonAction>> { ["Tap"] = [new NavigateAction("main")] })]),
        ]);
        var put = await client.PutAsync($"/devices/{Serial}/config",
            new StringContent(JsonSerializer.Serialize(config, JsonOptions.Default), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        return client;
    }

    private static async Task<Image> AssertPngAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(PngMagic, bytes[..8]);
        return Image.Load(bytes);
    }

    [Fact]
    public async Task BoardPreview_ReturnsAPng_OfTheBoard()
    {
        var client = await SeedAsync();

        var response = await client.GetAsync($"/devices/{Serial}/preview.png");
        using var image = await AssertPngAsync(response);

        Assert.Equal((5 * 72 + 6 * 6, 3 * 72 + 4 * 6), (image.Width, image.Height));
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.NotNull(response.Headers.ETag);
        // No deck is attached in tests, so the preview is composed from desired state.
        Assert.Equal("desired-state", response.Headers.GetValues("X-Preview-Source").Single());
    }

    [Fact]
    public async Task DeviceWithoutConfig_PreviewsAsABlackBoard()
    {
        await _factory.SeedDeviceAsync("SN_BLANK");
        var client = _factory.CreateAuthenticatedClient();

        using var png = await AssertPngAsync(await client.GetAsync("/devices/SN_BLANK/preview.png"));
        using var image = png.CloneAs<Rgba32>();
        Assert.Equal(new Rgba32(0, 0, 0), image[6 + 36, 6 + 36]);   // key 0: black
    }

    [Fact]
    public async Task PageAndScale_Parameters()
    {
        var client = await SeedAsync();

        var main = await (await client.GetAsync($"/devices/{Serial}/preview.png")).Content.ReadAsByteArrayAsync();
        var other = await client.GetAsync($"/devices/{Serial}/preview.png?page=other");
        using var _ = await AssertPngAsync(other);
        Assert.NotEqual(main, await other.Content.ReadAsByteArrayAsync());

        using var big = await AssertPngAsync(await client.GetAsync($"/devices/{Serial}/preview.png?scale=2"));
        Assert.Equal(2 * (5 * 72 + 6 * 6), big.Width);

        var badScale = await client.GetAsync($"/devices/{Serial}/preview.png?scale=3");
        Assert.Equal(HttpStatusCode.BadRequest, badScale.StatusCode);
        Assert.Equal("application/problem+json", badScale.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task KeyPreview_ReturnsOneKey()
    {
        var client = await SeedAsync();

        using var key = await AssertPngAsync(await client.GetAsync($"/devices/{Serial}/keys/0.png"));
        Assert.Equal((72, 72), (key.Width, key.Height));

        using var big = await AssertPngAsync(await client.GetAsync($"/devices/{Serial}/keys/4.png?page=other&scale=2"));
        Assert.Equal((144, 144), (big.Width, big.Height));
    }

    [Theory]
    [InlineData("/devices/GHOST/preview.png")]
    [InlineData("/devices/GHOST/keys/0.png")]
    [InlineData("/devices/SN_PREV/preview.png?page=nope")]
    [InlineData("/devices/SN_PREV/keys/15.png")]
    public async Task UnknownDevicePageOrKey_Returns404Problem(string path)
    {
        var client = await SeedAsync();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task IfNoneMatch_WithTheCurrentETag_Returns304()
    {
        var client = await SeedAsync();
        var first = await client.GetAsync($"/devices/{Serial}/preview.png");
        var etag = first.Headers.ETag!;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/devices/{Serial}/preview.png");
        request.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, second.Headers.ETag);

        using var stale = new HttpRequestMessage(HttpMethod.Get, $"/devices/{Serial}/preview.png");
        stale.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"0000000000000000\""));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task Preview_NeedsTheApiKey()
    {
        await SeedAsync();
        var response = await _factory.CreateClient().GetAsync($"/devices/{Serial}/preview.png");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Preview_WorksUnderAPathBase()
    {
        using var proxied = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Proxy:PathBase"] = "/deck" })));
        await proxied.Services.GetRequiredService<CatalogueStore>().AppendDeviceAsync(new(
            Serial, "Stream Deck MK.2", 3, 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var client = proxied.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", StreamDeckApiFactory.TestApiKey);

        using var board = await AssertPngAsync(await client.GetAsync($"/deck/devices/{Serial}/preview.png"));
        using var key = await AssertPngAsync(await client.GetAsync($"/deck/devices/{Serial}/keys/0.png"));
        Assert.Equal((72, 72), (key.Width, key.Height));
    }

    [Fact]
    public async Task OpenApi_DescribesThePreviewEndpoints()
    {
        var doc = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync("/openapi.json"));
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/devices/{serial}/preview.png", out _));
        Assert.True(paths.TryGetProperty("/devices/{serial}/keys/{index}.png", out _));
    }
}
