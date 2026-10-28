using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace StreamDeckPilot.Tests.Web;

/// <summary>
/// A minimal OpenID Connect provider on a real local port, for the sign-in flow tests (#97):
/// discovery, JWKS, an authorize endpoint that signs the configured user in at once, a token
/// endpoint that checks PKCE and returns a signed ID token, and user info.
/// </summary>
public sealed class FakeOidcProvider : IAsyncDisposable
{
    public const string ClientId = "stream-deck-pilot";
    public const string ClientSecret = "fake-client-secret";

    private readonly WebApplication _app;
    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key" };
    private readonly ConcurrentDictionary<string, Grant> _grants = new();

    /// <summary>Who the next authorize request signs in, and the claims they get.</summary>
    public string Subject { get; set; } = "user-1";
    public string UserName { get; set; } = "alice";
    public Dictionary<string, object> ExtraClaims { get; set; } = new() { ["roles"] = new[] { "viewer" } };

    /// <summary>The last authorize request's redirect_uri, as the app sent it.</summary>
    public string? LastRedirectUri { get; private set; }

    public string Authority { get; }

    private FakeOidcProvider(WebApplication app, string authority)
    {
        _app = app;
        Authority = authority;
    }

    public static async Task<FakeOidcProvider> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeOidcProvider? provider = null;
        app.Use((context, next) => provider!.HandleAsync(context, next));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        provider = new FakeOidcProvider(app, address);
        return provider;
    }

    private async Task HandleAsync(HttpContext context, Func<Task> next)
    {
        var path = context.Request.Path.Value;
        switch (path)
        {
            case "/.well-known/openid-configuration":
                await context.Response.WriteAsJsonAsync(new Dictionary<string, object>
                {
                    ["issuer"] = Authority,
                    ["authorization_endpoint"] = $"{Authority}/authorize",
                    ["token_endpoint"] = $"{Authority}/token",
                    ["userinfo_endpoint"] = $"{Authority}/userinfo",
                    ["jwks_uri"] = $"{Authority}/jwks",
                    ["response_types_supported"] = new[] { "code" },
                    ["subject_types_supported"] = new[] { "public" },
                    ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                    ["code_challenge_methods_supported"] = new[] { "S256" },
                });
                return;

            case "/jwks":
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
                await context.Response.WriteAsJsonAsync(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
                return;

            case "/authorize":
                var q = context.Request.Query;
                LastRedirectUri = q["redirect_uri"];
                var code = Guid.NewGuid().ToString("N");
                _grants[code] = new Grant(q["nonce"]!, q["code_challenge"]!, q["redirect_uri"]!, Subject, UserName, new(ExtraClaims));
                context.Response.Redirect($"{q["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(q["state"]!)}");
                return;

            case "/token":
                var form = await context.Request.ReadFormAsync();
                if (!_grants.TryRemove(form["code"]!, out var grant) || form["client_id"] != ClientId
                    || form["client_secret"] != ClientSecret || form["redirect_uri"] != grant.RedirectUri
                    || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]!))) != grant.CodeChallenge)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = "invalid_grant" });
                    return;
                }
                await context.Response.WriteAsJsonAsync(new Dictionary<string, object>
                {
                    ["access_token"] = $"access-{grant.Subject}",
                    ["token_type"] = "Bearer",
                    ["expires_in"] = 300,
                    ["id_token"] = IdToken(grant),
                });
                return;

            case "/userinfo":
                var subject = context.Request.Headers.Authorization.ToString().Replace("Bearer access-", "");
                await context.Response.WriteAsJsonAsync(new { sub = subject });
                return;
        }
        await next();
    }

    private string IdToken(Grant grant)
    {
        var claims = new Dictionary<string, object>(grant.Claims)
        {
            ["sub"] = grant.Subject,
            ["preferred_username"] = grant.UserName,
            ["nonce"] = grant.Nonce,
        };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = ClientId,
            Claims = claims,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private sealed record Grant(string Nonce, string CodeChallenge, string RedirectUri, string Subject, string UserName, Dictionary<string, object> Claims);
}
