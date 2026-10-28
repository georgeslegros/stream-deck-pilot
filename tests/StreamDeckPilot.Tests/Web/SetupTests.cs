using System.Net;
using StreamDeckPilot.Core.Security;
using StreamDeckPilot.Web.Auth;

namespace StreamDeckPilot.Tests.Web;

/// <summary>
/// First-run setup (#97): creates the first admin, only for someone who enters the bootstrap API key,
/// only once, never over an unreadable accounts file, and without the key ever reaching a page or a log.
/// </summary>
public sealed class SetupTests
{
    private const string OwnerPassword = "a long enough password";

    private static Dictionary<string, string> Fields(string key, string user = "owner",
        string password = OwnerPassword, string? confirm = null) => new()
    {
        ["Input.ApiKey"] = key,
        ["Input.UserName"] = user,
        ["Input.Password"] = password,
        ["Input.Confirm"] = confirm ?? password,
    };

    [Fact]
    public async Task FreshInstall_SignInLeadsToSetup_WhichNeedsTheApiKey_AndClosesForGood()
    {
        await using var factory = new WebAuthFactory();
        var browser = factory.CreateFormClient();

        var signIn = await browser.Http.GetAsync("account/signin");
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/account/setup", signIn.Headers.Location!.AbsolutePath);
        Assert.Contains(factory.Logs.Lines, l => l.Contains("enter the API key"));

        var wrong = await browser.SubmitAsync("account/setup", "setup", Fields("not-the-key"));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("the service&#x27;s API key", await wrong.Content.ReadAsStringAsync());
        Assert.False(await factory.Users.AnyAsync());

        var created = await browser.SubmitAsync("account/setup", "setup", Fields(WebAuthFactory.ApiKey));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/", created.Headers.Location!.AbsolutePath);
        Assert.Equal(AuthRoles.Admin, (await factory.Users.VerifyAsync("owner", OwnerPassword))!.Role);
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/")).StatusCode);

        // Once an admin exists, setup is gone: the page redirects, and a post (even with the key) creates nobody.
        var again = factory.CreateFormClient();
        var setupPage = await again.Http.GetAsync("account/setup");
        Assert.Equal(HttpStatusCode.Redirect, setupPage.StatusCode);
        Assert.Equal("/account/signin", setupPage.Headers.Location!.AbsolutePath);
        var signInHtml = await again.GetHtmlAsync("account/signin");
        await again.PostAsync("account/setup", signInHtml, "setup", Fields(WebAuthFactory.ApiKey, user: "intruder"));
        Assert.Null(await factory.Users.FindAsync("intruder"));
    }

    [Fact]
    public async Task TheKey_IsOnlyReadFromTheFormBody_AndNeverRenderedBack()
    {
        await using var factory = new WebAuthFactory();
        var browser = factory.CreateFormClient();
        var html = await browser.GetHtmlAsync("account/setup");

        // The key in the URL, with an empty form field, doesn't count.
        var inQuery = await browser.PostAsync($"account/setup?ApiKey={WebAuthFactory.ApiKey}&Input.ApiKey={WebAuthFactory.ApiKey}",
            html, "setup", Fields(""));
        Assert.Contains("the service&#x27;s API key", await inQuery.Content.ReadAsStringAsync());
        Assert.False(await factory.Users.AnyAsync());

        // After an error the form comes back with the key and the passwords empty.
        var mismatch = await browser.SubmitAsync("account/setup", "setup", Fields(WebAuthFactory.ApiKey, confirm: "something else entirely"));
        var body = await mismatch.Content.ReadAsStringAsync();
        Assert.Contains("don&#x27;t match", body);
        Assert.DoesNotContain(WebAuthFactory.ApiKey, body);
        Assert.DoesNotContain(OwnerPassword, body);

        var wrongKey = await browser.SubmitAsync("account/setup", "setup", Fields("not-the-key-but-close"));
        Assert.DoesNotContain("not-the-key-but-close", await wrongKey.Content.ReadAsStringAsync());

        var shortPassword = await browser.SubmitAsync("account/setup", "setup", Fields(WebAuthFactory.ApiKey, password: "short"));
        Assert.Contains("at least 12 characters", await shortPassword.Content.ReadAsStringAsync());
        Assert.False(await factory.Users.AnyAsync());
    }

    [Fact]
    public async Task Setup_NeedsTheAntiforgeryToken_AndTheAppsOwnOrigin()
    {
        await using var factory = new WebAuthFactory();
        var browser = factory.CreateFormClient();

        var noToken = await browser.Http.PostAsync("account/setup", new FormUrlEncodedContent(
            new Dictionary<string, string>(Fields(WebAuthFactory.ApiKey)) { ["_handler"] = "setup" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "account/setup")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(Fields(WebAuthFactory.ApiKey))
            {
                ["_handler"] = "setup",
                ["__RequestVerificationToken"] = FormClient.Token(await browser.GetHtmlAsync("account/setup")),
            }),
        };
        crossSite.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.Http.SendAsync(crossSite)).StatusCode);
        Assert.False(await factory.Users.AnyAsync());
    }

    [Fact]
    public async Task Setup_IsRateLimited_EvenWithTheRightKey()
    {
        await using var factory = new WebAuthFactory(new() { ["Auth:AttemptLimit"] = "2" });
        var browser = factory.CreateFormClient();
        await browser.SubmitAsync("account/setup", "setup", Fields("wrong-1"));
        await browser.SubmitAsync("account/setup", "setup", Fields("wrong-2"));

        var limited = await browser.SubmitAsync("account/setup", "setup", Fields(WebAuthFactory.ApiKey));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.False(await factory.Users.AnyAsync());
    }

    [Fact]
    public async Task WithoutABootstrapKey_SetupIsImpossible_AndSaysWhy()
    {
        await using var factory = new WebAuthFactory(new() { ["ApiKey"] = "" });
        var browser = factory.CreateFormClient();

        var html = await browser.GetHtmlAsync("account/setup");
        Assert.Contains("data-testid=\"setup-unavailable\"", html);
        Assert.DoesNotContain("data-testid=\"setup-key\"", html);
        Assert.Contains(factory.Logs.Lines, l => l.Contains("needs the bootstrap API key, which isn't set"));

        // The page has no form; a hand-made post (token from another page) creates nobody either.
        await browser.PostAsync("account/setup", await browser.GetHtmlAsync("account/denied"), "setup", Fields(""));
        Assert.False(await factory.Users.AnyAsync());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schemaVersion":99,"users":[]}""")]
    public async Task UnreadableAccountsFile_FailsClosed_NoSetup_NoLocalSignIn_FileUntouched(string content)
    {
        await using var factory = new WebAuthFactory();
        var path = Path.Combine(factory.StorageDirectory, "auth", "users.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        var browser = factory.CreateFormClient();

        var setup = await browser.Http.GetAsync("account/setup");
        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
        Assert.Equal("/account/signin", setup.Headers.Location!.AbsolutePath);

        var signInHtml = await browser.GetHtmlAsync("account/signin");   // not sent on to setup
        Assert.Contains("accounts file can&#x27;t be read", signInHtml);
        var signIn = await browser.SignInAsync("anyone", "any password 123");
        Assert.Contains("accounts file can&#x27;t be read", await signIn.Content.ReadAsStringAsync());

        await browser.PostAsync("account/setup", signInHtml, "setup", Fields(WebAuthFactory.ApiKey));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Contains(factory.Logs.Lines, l => l.StartsWith("Error") && l.Contains("can't be read"));
    }

    [Fact]
    public async Task Logs_NeverContainTheKey_OrPasswords()
    {
        const string wrongPassword = "a wrong password 456";
        const string wrongKey = "a-wrong-api-key-789";
        await using var factory = new WebAuthFactory();
        var browser = factory.CreateFormClient();

        await browser.SubmitAsync("account/setup", "setup", Fields(wrongKey));
        await browser.SubmitAsync("account/setup", "setup", Fields(WebAuthFactory.ApiKey, password: WebAuthFactory.AdminPassword));
        var other = factory.CreateFormClient();
        await other.SignInAsync("owner", wrongPassword);
        await other.SignInAsync("owner", WebAuthFactory.AdminPassword);

        var lines = factory.Logs.Lines;
        Assert.Contains(lines, l => l.Contains("Request starting") && l.Contains("/account/setup"));   // the hosting log was captured
        Assert.Contains(lines, l => l.Contains("StreamDeckPilot.Audit") && l.Contains("invalid_api_key"));
        Assert.Contains(lines, l => l.Contains("StreamDeckPilot.Audit") && l.Contains("admin account owner created"));
        Assert.Contains(lines, l => l.Contains("StreamDeckPilot.Audit") && l.Contains("owner signed in with local as admin"));
        Assert.Contains(lines, l => l.Contains("StreamDeckPilot.Audit") && l.Contains("invalid_credentials"));
        Assert.DoesNotContain(lines, l => l.Contains(WebAuthFactory.ApiKey));
        Assert.DoesNotContain(lines, l => l.Contains(wrongKey));
        Assert.DoesNotContain(lines, l => l.Contains(WebAuthFactory.AdminPassword));
        Assert.DoesNotContain(lines, l => l.Contains(wrongPassword));
    }

    [Fact]
    public void KeyComparison_IsExact_WhateverTheLength()
    {
        Assert.True(SecretComparer.Matches("the-key", "the-key"));
        Assert.False(SecretComparer.Matches("the-key", "the-ke"));
        Assert.False(SecretComparer.Matches("the-key", "the-key-and-more"));
        Assert.False(SecretComparer.Matches("the-key", "THE-KEY"));
        Assert.False(SecretComparer.Matches("the-key", ""));
        Assert.False(SecretComparer.Matches("", ""));
    }
}
