using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamDeckPilot.Web.Auth;

namespace StreamDeckPilot.Tests.Web;

/// <summary>
/// The app with the web UI on (#97). Pass <paramref name="storageDirectory"/> to share the data
/// volume between two app instances (a restart); it is then left for the caller to delete.
/// </summary>
public sealed class WebAuthFactory(
    Dictionary<string, string?>? settings = null,
    string? storageDirectory = null,
    Action<IServiceCollection>? testServices = null) : WebApplicationFactory<Program>
{
    public const string ApiKey = "auth-test-api-key-123";
    public const string AdminUser = "admin";
    public const string AdminPassword = "correct horse battery";
    public const string ViewerUser = "viewer";
    public const string ViewerPassword = "viewer password 123";

    private readonly bool _ownsStorage = storageDirectory is null;
    public string StorageDirectory { get; } = storageDirectory ?? Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    /// <summary>Everything the app logged, from every category, at Debug and up.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets();
        // UseSetting so Program sees them while building services.
        builder.UseSetting("Web:Enabled", "true");
        builder.UseSetting("ApiKey", ApiKey);
        builder.UseSetting("Storage:BaseDirectory", StorageDirectory);
        foreach (var (key, value) in settings ?? [])
            builder.UseSetting(key, value);
        // The app logs through Serilog; a plain logger factory sends everything to Logs instead.
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs)));
            testServices?.Invoke(services);
        });
    }

    public LocalUserStore Users => Services.GetRequiredService<LocalUserStore>();

    public async Task SeedUsersAsync()
    {
        await Users.CreateAsync(AdminUser, AdminPassword, AuthRoles.Admin);
        await Users.CreateAsync(ViewerUser, ViewerPassword, AuthRoles.Viewer);
    }

    /// <summary>A browser-like client: keeps cookies, doesn't follow redirects, asks for HTML.</summary>
    public FormClient CreateFormClient(string? baseAddress = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri(baseAddress ?? "http://localhost/"),
        });
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        return new FormClient(client);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_ownsStorage && Directory.Exists(StorageDirectory))
            Directory.Delete(StorageDirectory, recursive: true);
    }
}

/// <summary>Fills in server-rendered forms the way a browser does: anti-forgery token and form name included.</summary>
public sealed partial class FormClient(HttpClient http)
{
    public HttpClient Http => http;

    public async Task<string> GetHtmlAsync(string path)
    {
        var response = await http.GetAsync(path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {response.Headers.Location}");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Loads <paramref name="path"/>, then posts its form named <paramref name="formName"/> with the given fields.</summary>
    public async Task<HttpResponseMessage> SubmitAsync(string path, string formName, IDictionary<string, string> fields)
    {
        var html = await GetHtmlAsync(path);
        return await PostAsync(path, html, formName, fields);
    }

    public Task<HttpResponseMessage> PostAsync(string path, string html, string formName, IDictionary<string, string> fields)
    {
        var form = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = Token(html),
            ["_handler"] = formName,
        };
        return http.PostAsync(path, new FormUrlEncodedContent(form));
    }

    public Task<HttpResponseMessage> SignInAsync(string user, string password, string path = "account/signin") =>
        SubmitAsync(path, "signin", new Dictionary<string, string>
        {
            ["Input.UserName"] = user,
            ["Input.Password"] = password,
        });

    public static string Token(string html) =>
        TokenPattern().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value)
            : throw new Xunit.Sdk.XunitException("No anti-forgery token in the page.");

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
