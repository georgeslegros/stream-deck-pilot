using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamDeckPilot.Web.Auth;

namespace StreamDeckPilot.Tests.Web;

/// <summary>
/// Sessions end when they should (#97): a deleted, demoted or re-passworded local account loses
/// access within <c>Auth:RevalidateMinutes</c>, and every session ends after <c>Auth:MaxSessionDays</c>
/// however active it is.
/// </summary>
public sealed class SessionTests
{
    private static (WebAuthFactory App, FakeTimeProvider Clock) WithClock(Dictionary<string, string?>? settings = null)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return (new WebAuthFactory(settings, testServices: s => s.AddSingleton<TimeProvider>(clock)), clock);
    }

    private static async Task<bool> SignedInAsync(FormClient browser) =>
        (await browser.Http.GetAsync("/")).StatusCode == HttpStatusCode.OK;

    [Theory]
    [InlineData("deleted")]
    [InlineData("demoted")]
    [InlineData("password changed")]
    public async Task LocalAccountChanges_EndItsSessions_WithinTheRevalidationInterval(string change)
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.AdminUser, WebAuthFactory.AdminPassword);
        Assert.True(await SignedInAsync(browser));

        Assert.True(change switch
        {
            "deleted" => await factory.Users.DeleteAsync(WebAuthFactory.AdminUser),
            "demoted" => await factory.Users.SetRoleAsync(WebAuthFactory.AdminUser, AuthRoles.Viewer),
            _ => await factory.Users.SetPasswordAsync(WebAuthFactory.AdminUser, "a brand new password"),
        });

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await SignedInAsync(browser));    // not checked again yet
        clock.Advance(TimeSpan.FromMinutes(4));       // past Auth:RevalidateMinutes (5)
        Assert.False(await SignedInAsync(browser));
        Assert.Contains(factory.Logs.Lines, l => l.Contains("Session ended"));
        Assert.False(await SignedInAsync(browser));   // the cookie is gone, not just refused once
    }

    [Fact]
    public async Task UnchangedAccount_StaysSignedIn_AcrossRevalidations()
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.ViewerUser, WebAuthFactory.ViewerPassword);

        for (var i = 0; i < 6; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.True(await SignedInAsync(browser), $"signed out after {(i + 1) * 6} min");
        }
    }

    [Fact]
    public async Task MaxLifetime_EndsEvenAnActiveSession()
    {
        var (factory, clock) = WithClock(new() { ["Auth:MaxSessionDays"] = "1" });
        await using var _ = factory;
        await factory.SeedUsersAsync();
        var browser = factory.CreateFormClient();
        await browser.SignInAsync(WebAuthFactory.ViewerUser, WebAuthFactory.ViewerPassword);

        // Active all day: a request every 4 hours keeps the 12 h sliding session alive…
        for (var hours = 4; hours < 24; hours += 4)
        {
            clock.Advance(TimeSpan.FromHours(4));
            Assert.True(await SignedInAsync(browser), $"signed out after {hours} h");
        }
        // …until a day after signing in.
        clock.Advance(TimeSpan.FromHours(4));
        Assert.False(await SignedInAsync(browser));
        Assert.Contains(factory.Logs.Lines, l => l.Contains("Session ended") && l.Contains("max_lifetime"));
    }

    [Fact]
    public async Task OpenTabs_AreRevalidatedWithTheSameChecks()
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        await factory.SeedUsersAsync();   // starts the host
        var validator = factory.Services.GetRequiredService<SessionValidator>();
        var admin = (await factory.Users.FindAsync(WebAuthFactory.AdminUser))!;
        var local = AuthRoles.Principal(admin, clock.GetUtcNow());
        var provider = AuthRoles.Principal("oidc:p:1", "alice", AuthRoles.Viewer, "oidc:p", clock.GetUtcNow());

        Assert.Null(await validator.ProblemAsync(local, checkAccount: true));
        Assert.Null(await validator.ProblemAsync(provider, checkAccount: true));
        await factory.Users.SetRoleAsync(WebAuthFactory.AdminUser, AuthRoles.Viewer);
        Assert.Equal("account_changed", await validator.ProblemAsync(local, checkAccount: true));
        await factory.Users.DeleteAsync(WebAuthFactory.AdminUser);
        Assert.Equal("account_deleted", await validator.ProblemAsync(local, checkAccount: true));

        clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal("max_lifetime", await validator.ProblemAsync(provider, checkAccount: true));
    }

    [Fact]
    public async Task SignIn_IsAlsoRateLimitedPerUserName_AcrossAddresses()
    {
        await using var factory = new WebAuthFactory(new() { ["Auth:AttemptLimit"] = "100", ["Auth:UserAttemptLimit"] = "2" });
        await factory.SeedUsersAsync();

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await factory.CreateFormClient().SignInAsync(WebAuthFactory.AdminUser, "wrong password 123")).StatusCode);
        var limited = await factory.CreateFormClient().SignInAsync(WebAuthFactory.AdminUser.ToUpperInvariant(), WebAuthFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        // Another user isn't affected.
        Assert.Equal(HttpStatusCode.Redirect, (await factory.CreateFormClient().SignInAsync(WebAuthFactory.ViewerUser, WebAuthFactory.ViewerPassword)).StatusCode);
    }
}
