# Signing in to the web UI

When the web UI is on (`Web__Enabled=true`), **every page needs a signed-in user**. There is no
anonymous UI. People sign in with an OpenID Connect provider (Entra ID, Keycloak, Authentik,
Authelia, Zitadel, Google…) or with a local account. The REST API is unchanged: it still takes the
`X-Api-Key` header (named, revocable keys come with #98).

- [Roles](#roles)
- [First run: local admin](#first-run-the-local-admin)
- [Local accounts](#local-accounts)
- [OpenID Connect providers](#openid-connect-providers): [Keycloak](#keycloak), [Entra ID](#microsoft-entra-id)
- [Sessions and security](#sessions-and-security)
- [Settings](#settings)

## Roles

| Role | Can |
|---|---|
| `admin` | Everything a viewer can, plus (as the editor pages arrive) change config, press keys through the mirror and manage API keys. |
| `viewer` | Read-only: the dashboard and deck pages. |

Today's pages are all read-only, so both roles see the same thing. A provider user who maps to
neither role is refused (see `DefaultRole` below).

## First run: the local admin

A fresh install is never locked out. When local accounts are on (the default when no provider is
configured) and no account exists yet, the sign-in page leads to **first-run setup**, which creates
the admin. Setup asks for a **one-time setup token** that the service prints to its log at startup:

```
No local account exists yet. Open https://deck.example.com/account/setup and enter this one-time setup token to create the admin: 3q2-…
```

```bash
docker logs stream-deck-pilot 2>&1 | grep "setup token"
```

Only someone who can read the service's logs can finish setup, so a stranger on your network who
opens the page first can't claim the install. The token works once; every restart before setup
prints a new one. After setup the page is gone. The URL in the message uses `Web__PublicOrigin`
when it is set.

## Local accounts

Stored in `{Storage__BaseDirectory}/auth/users.json`, written atomically like the other files.
Passwords are kept as ASP.NET Core Identity hashes (PBKDF2 with a per-user salt), never in clear.
The minimum length is 12 characters (`Auth__Local__MinimumPasswordLength`).

`Auth__Local__Enabled` controls them: unset (default) turns them on only when no provider is
configured; `true` keeps them next to providers (a fallback if the provider is down); `false` turns
them off. With no provider and local accounts off, the service refuses to start.

There is no account management page yet: setup creates one admin. More users come with the
settings pages; until then, people sign in with a provider.

## OpenID Connect providers

Each provider is an entry in `Auth:Providers`. The sign-in page shows one button per provider, so
switching provider is a settings change. The service uses the authorization code flow with PKCE.

**Required with any provider:** `Web__PublicOrigin`, the address people type
(`https://deck.example.com`, scheme and host only; a sub-path comes from `Proxy__PathBase`). Redirect
URIs are built from it, never from the request's `Host` or `X-Forwarded-Host` headers, so a spoofed
header can't send a sign-in code elsewhere. The redirect URI to register at the provider is:

```
{Web__PublicOrigin}{Proxy__PathBase}/signin-oidc-{name}
e.g. https://deck.example.com/signin-oidc-keycloak
```

Serve the UI over **HTTPS**: the sign-in flow's cookies are marked `Secure`, so it can't complete
over plain HTTP.

| Setting (per provider, `Auth__Providers__0__…`) | Meaning | Default |
|---|---|---|
| `Name` | Short id used in URLs: letters, digits, dashes. | required |
| `DisplayName` | Button label. | `Name` |
| `Authority` | Issuer URL; its `/.well-known/openid-configuration` is read. | required |
| `ClientId` | The app's client id at the provider. | required |
| `ClientSecret` | The client secret. Pass it as an environment variable or Docker secret. | none (public client) |
| `Scopes__0`, `__1`, … | Scopes to request. Setting any replaces the default. | `openid profile email` |
| `RoleClaim` | Claim that carries roles or groups, from the ID token or user info. A dotted path reads inside a JSON claim (`realm_access.roles`). | `roles` |
| `AdminValues__0`, … | Claim values that make a user `admin`. | `admin` |
| `ViewerValues__0`, … | Claim values that make a user `viewer`. | `viewer` |
| `DefaultRole` | Role for a user who matches neither list: unset refuses them; `viewer` lets everyone the provider signs in look. | unset |
| `RequireHttpsMetadata` | Require HTTPS for the provider's metadata. Only turn off for a local test provider. | `true` |

`admin` wins when a user matches both lists. Values compare case-insensitively.

Sign-out ends the session in this app only; the provider's own session stays.

### Keycloak

Create a client (`stream-deck-pilot`, client authentication on, standard flow), with the valid
redirect URI `https://deck.example.com/signin-oidc-keycloak`. Create realm roles `deck-admin` and
`deck-viewer` and assign them. Realm roles are in the `realm_access` claim of the ID token when the
client's "roles" scope maps them there (Client scopes → roles → realm roles mapper, "Add to ID token").

```yaml
environment:
  - Web__Enabled=true
  - Web__PublicOrigin=https://deck.example.com
  - Auth__Providers__0__Name=keycloak
  - Auth__Providers__0__DisplayName=Keycloak
  - Auth__Providers__0__Authority=https://id.example.com/realms/home
  - Auth__Providers__0__ClientId=stream-deck-pilot
  - Auth__Providers__0__ClientSecret=${KEYCLOAK_CLIENT_SECRET}
  - Auth__Providers__0__RoleClaim=realm_access.roles
  - Auth__Providers__0__AdminValues__0=deck-admin
  - Auth__Providers__0__ViewerValues__0=deck-viewer
```

Authentik, Authelia and Zitadel work the same way: point `Authority` at the provider's issuer for
this application and set `RoleClaim` to the claim that holds groups (often `groups`).

### Microsoft Entra ID

Register an application (web platform) with the redirect URI
`https://deck.example.com/signin-oidc-entra`, create a client secret, and define app roles `admin`
and `viewer` (Users/Groups members). Assign users or groups to the roles under the enterprise
application. Entra puts assigned app roles in the `roles` claim of the ID token.

```yaml
environment:
  - Web__Enabled=true
  - Web__PublicOrigin=https://deck.example.com
  - Auth__Providers__0__Name=entra
  - Auth__Providers__0__DisplayName=Microsoft
  - Auth__Providers__0__Authority=https://login.microsoftonline.com/<tenant-id>/v2.0
  - Auth__Providers__0__ClientId=<application-id>
  - Auth__Providers__0__ClientSecret=${ENTRA_CLIENT_SECRET}
```

The defaults (`RoleClaim=roles`, `admin`, `viewer`) match those app role values.

## Sessions and security

- **Session cookie** `sdp.session`: `HttpOnly`, `SameSite=Lax` (needed for the provider's redirect
  back), and `Secure` whenever the request is HTTPS. Behind a TLS-terminating proxy that comes from
  `X-Forwarded-Proto`, so the proxy must be trusted ([reverse proxy](reverse-proxy.md)). A sign-in
  over plain HTTP logs a warning. The session lasts `Auth__SessionHours` (12) without activity and
  renews while used; it survives browser restarts.
- **Sessions survive service restarts and upgrades**: the keys that sign cookies, anti-forgery
  tokens and the Blazor circuit (ASP.NET Core Data Protection) are kept on the data volume, in
  `{Storage__BaseDirectory}/dataprotection-keys`. They are **unencrypted at rest** unless you set
  `DataProtection__CertificatePath` (a PFX with a private key) and `DataProtection__CertificatePassword`;
  the service logs which at startup, and without a certificate the framework also logs "No XML
  encryptor configured". Protect the volume: anyone who can read those files can forge a session.
- **Anti-forgery**: the sign-in, setup and sign-out forms carry an anti-forgery token. Sign-out is a
  form post, not a link, so another site can't sign you out.
- **Cross-site WebSocket hijacking**: the Blazor circuit (`/_blazor`), the live hub (`/hubs/live`)
  and the event stream (`/api/events`) refuse a request whose `Origin` isn't the app's own:
  `Web__PublicOrigin` when set, otherwise the request's scheme and host (as resolved through trusted
  proxies). Requests without an `Origin` (scripts using the API key) pass.
- **Rate limiting**: `Auth__AttemptLimit` (5) sign-in attempts, and as many setup attempts, per
  client address per `Auth__AttemptWindowSeconds` (300). The address is the one resolved through
  **trusted** proxies only. The default `Proxy__TrustedNetworks` trusts every private range, which
  lets any host on your LAN set `X-Forwarded-For` and pick a new address per attempt; set
  `Proxy__TrustedProxies__0` to your proxy's address (and `Proxy__TrustedNetworks__0` to a narrow
  range) so only the proxy can.
- **Audit log**: sign-ins, refusals, setup and sign-outs are logged under the
  `StreamDeckPilot.Audit` category with the method, role and client address. Passwords and tokens
  are never logged; refusals don't log the user name either (people type passwords into it). Each
  refusal is also a `SignInFailed` [live event](live-events.md).
- The live hub and event stream accept a signed-in session as well as the API key, so a browser
  page of this app can use them. The rest of the REST API still needs the key.

## Settings

| Variable | Meaning | Default |
|---|---|---|
| `Web__PublicOrigin` | The UI's public address, scheme and host only. Required with a provider. | unset |
| `Auth__Providers__N__…` | OpenID Connect providers, see above. | none |
| `Auth__Local__Enabled` | Local accounts: unset = on when no provider; `true`/`false`. | unset |
| `Auth__Local__MinimumPasswordLength` | Minimum local password length. | `12` |
| `Auth__SessionHours` | Session lifetime without activity (sliding). | `12` |
| `Auth__AttemptLimit` | Sign-in (and setup) attempts per address per window. | `5` |
| `Auth__AttemptWindowSeconds` | The rate-limit window. | `300` |
| `DataProtection__CertificatePath` | PFX that encrypts the Data Protection keys at rest. | unset (unencrypted) |
| `DataProtection__CertificatePassword` | Its password. | unset |

Bad settings (no way to sign in, a provider without `Web__PublicOrigin`, a malformed origin or
provider) stop the service at startup with a message naming the setting.
