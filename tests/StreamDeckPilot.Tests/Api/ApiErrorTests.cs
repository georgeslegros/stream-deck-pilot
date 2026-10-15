using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace StreamDeckPilot.Tests.Api;

/// <summary>
/// Error responses share one shape: an RFC 7807 problem with <c>status</c> and <c>title</c> (#81);
/// malformed bodies are 400s rather than 500s (#78); concurrent PUTs don't fail (#79).
/// </summary>
public sealed class ApiErrorTests : IAsyncDisposable
{
    private readonly StreamDeckApiFactory _factory = new();

    async ValueTask IAsyncDisposable.DisposeAsync() =>
        await ((IAsyncDisposable)_factory).DisposeAsync();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        return problem;
    }

    // --- One shape for every error path (#81) ---

    public static TheoryData<string, string, string?, HttpStatusCode> ErrorPaths => new()
    {
        { "GET", "/devices/GHOST/status", null, HttpStatusCode.NotFound },
        { "GET", "/devices/GHOST/config", null, HttpStatusCode.NotFound },
        { "GET", "/devices/GHOST/active-page", null, HttpStatusCode.NotFound },
        { "PUT", "/devices/GHOST/config", """{"schemaVersion":2,"serial":"GHOST","pages":[]}""", HttpStatusCode.BadRequest },
        { "POST", "/devices/GHOST/navigate", """{"pageId":"main"}""", HttpStatusCode.NotFound },
        { "POST", "/devices/SN900/navigate", """{"pageId":""}""", HttpStatusCode.BadRequest },
        { "POST", "/devices/GHOST/force-render", null, HttpStatusCode.NotFound },
        { "POST", "/devices/SN900/force-render", null, HttpStatusCode.Conflict },
        { "GET", "/devices/GHOST/images", null, HttpStatusCode.NotFound },
        { "DELETE", "/devices/GHOST/images/icon.png", null, HttpStatusCode.NotFound },
        { "DELETE", "/devices/SN900/images/missing.png", null, HttpStatusCode.NotFound },
        { "POST", "/devices/SN900/images", null, HttpStatusCode.BadRequest },
        { "POST", "/config/upgrade", "not-json", HttpStatusCode.BadRequest },
        { "POST", "/config/upgrade", """{"schemaVersion":0}""", HttpStatusCode.UnprocessableEntity },
        { "GET", "/no-such-route", null, HttpStatusCode.NotFound },
    };

    [Theory]
    [MemberData(nameof(ErrorPaths))]
    public async Task ErrorPath_ReturnsProblemDetails(string method, string path, string? body, HttpStatusCode expected)
    {
        await _factory.SeedDeviceAsync("SN900");
        var client = _factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = Json(body);

        await AssertProblemAsync(await client.SendAsync(request), expected);
    }

    [Fact]
    public async Task MissingApiKey_IsAProblemWithItsErrorCode()
    {
        var response = await _factory.CreateClient().GetAsync("/devices");

        var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("invalid_api_key", problem.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ValidationFailure_KeepsTheErrorsList()
    {
        await _factory.SeedDeviceAsync("SN901");
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PutAsync("/devices/SN901/config", Json("""
            {"schemaVersion":2,"serial":"SN901","pages":[{"pageType":"ButtonGrid","pageId":"main","buttons":[
              {"buttonId":"a","keyIndex":99,"pageId":"main","display":{},"rules":[],"gestures":{}},
              {"buttonId":"b","keyIndex":98,"pageId":"main","display":{},"rules":[],"gestures":{}}]}]}
            """));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        var errors = problem.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Contains("Button 'a'", errors[0]);
        Assert.Contains("Button 'b'", errors[1]);
        Assert.Contains("Button 'a'", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UnhandledException_Is500ProblemWithoutExceptionDetails()
    {
        // A config file the store can't parse makes LoadAsync throw: nothing on the request path
        // catches it, so it reaches the global exception handler.
        Directory.CreateDirectory(Path.Combine(_factory.StorageDir, "config"));
        await File.WriteAllTextAsync(Path.Combine(_factory.StorageDir, "config", "SN902.json"), "{ not json");
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/devices/SN902/config");

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("JsonException", raw);
        Assert.DoesNotContain("   at ", raw); // no stack trace
        Assert.DoesNotContain(_factory.StorageDir, raw);
    }

    [Fact]
    public async Task ImageUpload_UnknownSerial_Is404AndCreatesNothing()
    {
        var client = _factory.CreateAuthenticatedClient();
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Icons.TestImages.Encode("png")), "file", "icon.png");

        var response = await client.PostAsync("/devices/GHOST/images", form);

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
        Assert.False(Directory.Exists(Path.Combine(_factory.StorageDir, "images", "GHOST")));
    }

    // --- Malformed bodies are 400s, never 500s (#78) ---

    [Theory]
    [InlineData("""{"schemaVersion":2,"serial":"SN910","pages":[{"pageId":"main","buttons":[]}]}""", "Invalid JSON")]
    [InlineData("""{"schemaVersion":2,"serial":"SN910","pages":[{"pageType":"ButtonGrid","pageId":"main","buttons":[{"buttonId":"p","keyIndex":0,"pageId":"main","display":{},"rules":[],"gestures":{"Tap":[{"topic":"t"}]}}]}]}""", "Invalid JSON")]
    [InlineData("""{"schemaVersion":2,"serial":"SN910"}""", "pages is required")]
    [InlineData("""{"schemaVersion":2,"serial":"SN910","pages":[null]}""", "Page 1 is null")]
    [InlineData("""{"schemaVersion":2,"serial":"SN910","pages":[{"pageType":"ButtonGrid","pageId":"main"}]}""", "Page 'main': buttons is required")]
    [InlineData("""{"schemaVersion":2,"serial":"SN910","pages":[{"pageType":"ButtonGrid","pageId":"main","buttons":[null]}]}""", "Page 'main' button 1 is null")]
    [InlineData("""{"schemaVersion":2,"serial":"SN910","pages":[{"pageType":"ButtonGrid","pageId":"main","buttons":[{"buttonId":"p","keyIndex":0,"pageId":"main","display":{},"rules":[null],"gestures":{}}]}]}""", "Button 'p' rule 1 is null")]
    [InlineData("""{"schemaVersion":2,"serial":"SN910","pages":[{"pageType":"ButtonGrid","pageId":"main","buttons":[{"buttonId":"p","keyIndex":0,"pageId":"main","display":{},"rules":[],"gestures":{"Tap":null}}]}]}""", "Button 'p' gesture 'Tap': the action list is required")]
    [InlineData("""{"schemaVersion":2,"serial":"SN910","pages":[{"pageType":"ButtonGrid","pageId":"main","buttons":[{"buttonId":"p","keyIndex":0,"pageId":"main","display":{},"rules":[],"gestures":{"Tap":[null]}}]}]}""", "Button 'p' gesture 'Tap' action 1 is null")]
    public async Task PutConfig_MalformedBody_Returns400AndSavesNothing(string body, string expectedError)
    {
        await _factory.SeedDeviceAsync("SN910");
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PutAsync("/devices/SN910/config", Json(body));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains(expectedError, problem.GetProperty("errors")[0].GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/devices/SN910/config")).StatusCode);
    }

    [Theory]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{}", "no 'schemaVersion'")]
    [InlineData("""{"schemaVersion":"x"}""", "must be an integer")]
    [InlineData("""{"schemaVersion":1.5}""", "must be an integer")]
    public async Task Upgrade_MalformedDocument_Returns400(string body, string expectedDetail)
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/config/upgrade", Json(body));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains(expectedDetail, problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Upgrade_BelowFloor_Is422WithItsErrorCode()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/config/upgrade", Json("""{"schemaVersion":0,"serial":"SN1","pages":[]}"""));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("unsupported_schema_version", problem.GetProperty("error").GetString());
    }

    // --- Concurrent PUTs (#79) ---

    [Fact]
    public async Task ConcurrentPuts_ForOneDevice_AllSucceed_AndOneOfThemIsPersisted()
    {
        await _factory.SeedDeviceAsync("SN920");
        var client = _factory.CreateAuthenticatedClient();
        string Body(int i) => $$$"""
            {"schemaVersion":2,"serial":"SN920","pages":[{"pageType":"ButtonGrid","pageId":"main","buttons":[
              {"buttonId":"b{{{i}}}","keyIndex":{{{i % 15}}},"pageId":"main","display":{},"rules":[],"gestures":{}}]}]}
            """;

        var responses = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => client.PutAsync("/devices/SN920/config", Json(Body(i)))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        var saved = await client.GetFromJsonAsync<JsonElement>("/devices/SN920/config");
        var buttonId = saved.GetProperty("pages")[0].GetProperty("buttons")[0].GetProperty("buttonId").GetString();
        Assert.Matches("^b([0-9]|[1-3][0-9])$", buttonId);
        Assert.Empty(Directory.GetFiles(Path.Combine(_factory.StorageDir, "config"), "*.tmp"));
    }
}
