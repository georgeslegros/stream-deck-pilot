# Signing in to the web UI

When the web UI is on (`Web__Enabled=true`), **every page needs a signed-in user**. There is no
anonymous UI. People sign in with an OpenID Connect provider (Entra ID, Keycloak, Authentik,
Authelia, Zitadel, Google…) or with a local account. The REST API takes an API key in the
`X-Api-Key` header (the bootstrap key or a [named key](#api-keys)) or a signed-in session.

- [Roles](#roles)
- [First run: local admin](#first-run-the-local-admin)
- [Local accounts](#local-accounts)
- [OpenID Connect providers](#openid-connect-providers): [Keycloak](#keycloak), [Entra ID](#microsoft-entra-id)
- [Sessions and security](#sessions-and-security)
- [API keys](#api-keys)
- [Settings](#settings)

## Roles

| Role | Can |
|---|---|
| `admin` | Everything a viewer can, plus [manage API keys](#api-keys), call the REST API with changes, and (as the editor pages arrive) change config and press keys through the mirror. |
| `viewer` | Read-only: the dashboard and deck pages, and read-only REST API calls. |

Admins also see the **API keys** page. A provider user who maps to neither role is refused (see
`DefaultRole` below).

## First run: the local admin

A fresh install is never locked out. When local accounts are on (the default when no provider is
configured) and no account exists yet, the sign-in page leads to **first-run setup**, which creates
the admin. Setup asks for the service's **API key** (the `ApiKey` setting, the bootstrap key): only
whoever configured the service has it, so a stranger on your network who opens the page first
can't claim the install. The startup log points at the page (never at the key):

```
No local account exists yet. Open https://deck.example.com/account/setup and enter the API key (the ApiKey setting) to create the admin.
```

- The key is read only from the setup form's body (anti-forgery token and the
  [Origin check](#sessions-and-security) apply), never from the URL. It is compared in constant time
  (both values hashed with SHA-256 first, so not even its length leaks), never written back into the
  page after an error, and never logged.
- Setup attempts are [rate-limited](#sessions-and-security) like sign-ins.
- **Once an admin exists, setup is gone for good**: the page sends you to sign-in and a post creates
  nobody.
- **No bootstrap key, no setup.** With `ApiKey` unset, or the bootstrap key disabled
  (`Auth__BootstrapKeyEnabled=false`, see [API keys](#api-keys)), the setup page says so and the
  startup log reports an error. To recover: set `ApiKey` and `Auth__BootstrapKeyEnabled=true` (or
  remove it) and restart, create the admin, then disable the bootstrap key again if you want; or
  configure an OpenID Connect provider.
- **An unreadable `auth/users.json`** (damaged, or from a newer version) fails closed: the service
  logs an error, local sign-in is refused and setup stays shut, so it can never start over on top of
  your accounts. Fix or move the file aside and restart.

The URL in the message uses `Web__PublicOrigin` when it is set.

## Local accounts

Stored in `{Storage__BaseDirectory}/auth/users.json`, written atomically like the other files.
Passwords are kept as ASP.NET Core Identity hashes (PBKDF2 with a per-user salt), never in clear.
The minimum length is 12 characters (`Auth__Local__MinimumPasswordLength`).

`Auth__Local__Enabled` controls them: unset (default) turns them on only when no provider is
configured; `true` keeps them next to providers (a fallback if the provider is down); `false` turns
them off. With no provider and local accounts off, the service refuses to start.

There is no account management page yet: setup creates one admin. More users come with the
settings pages; until then, people sign in with a provider.

The file is read once and then kept in memory. **If you edit it by hand** (say, to remove an account
or fix a damaged file), restart the service for the change to take effect.

Each account has a **security stamp** that changes when its password or role changes. A session
remembers the stamp it started with; see [revalidation](#sessions-and-security).

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
- **Sessions end when they should.**
  - Every `Auth__RevalidateMinutes` (5) a session is checked again: a local account that was
    deleted, or whose password or role changed (its security stamp), loses access. An open tab is
    re-checked on the same interval and sent to sign-in.
  - However active, a session ends `Auth__MaxSessionDays` (7) after sign-in. Provider users then
    sign in again, which picks up a role or group removed at the provider.
- **Sessions survive service restarts and upgrades**: the keys that sign cookies, anti-forgery
  tokens and the Blazor circuit (ASP.NET Core Data Protection) are kept on the data volume, in
  `{Storage__BaseDirectory}/dataprotection-keys`. They are **unencrypted at rest** unless you set
  `DataProtection__CertificatePath` (a PFX with a private key) and `DataProtection__CertificatePassword`;
  the service logs which at startup, and without a certificate the framework also logs "No XML
  encryptor configured". Protect the volume: anyone who can read those files can forge a session.
- **Anti-forgery**: the sign-in, setup and sign-out forms carry an anti-forgery token. Sign-out is a
  form post, not a link, so another site can't sign you out.
- **Origin check (cross-site WebSocket hijacking, cross-site form posts)**: the Blazor circuit
  (`/_blazor`), the live hub (`/hubs/live`), the event stream (`/api/events`) and the account forms
  (`/account/…`) refuse a request whose `Origin` isn't the app's own:
  `Web__PublicOrigin` when set, otherwise the request's scheme and host (as resolved through trusted
  proxies). Requests without an `Origin` (scripts using the API key) pass.
- **Rate limiting**: `Auth__AttemptLimit` (5) sign-in attempts, and as many setup attempts, per
  client address per `Auth__AttemptWindowSeconds` (300), and `Auth__UserAttemptLimit` (10) sign-in
  attempts per user name from any address. The per-user-name limit has a flip side: anyone who can
  reach the sign-in page can keep a known user (say `admin`) locked out for a window at a time by
  failing on purpose. If that matters more to you than slowing distributed guessing, set
  `Auth__UserAttemptLimit=0` to turn it off; the per-address limit still applies. The address is the one resolved through
  **trusted** proxies only. The default `Proxy__TrustedNetworks` trusts every private range, which
  lets any host on your LAN set `X-Forwarded-For` and pick a new address per attempt; set
  `Proxy__TrustedProxies__0` to your proxy's address (and `Proxy__TrustedNetworks__0` to a narrow
  range) so only the proxy can.
- **Audit log**: sign-ins, refusals, setup and sign-outs are logged under the
  `StreamDeckPilot.Audit` category with the method, role and client address. Passwords and tokens
  are never logged; refusals don't log the user name either (people type passwords into it). Each
  refusal is also a `SignInFailed` [live event](live-events.md).
- The REST API, the live hub and the event stream accept a signed-in session as well as an API key,
  so a browser page of this app can use them. See [sessions on the API](#sessions-on-the-api).

## API keys

Scripts and other programs use the REST API with a key in the `X-Api-Key` header (the live hub also
takes it as a `Bearer` token). A key is **never** accepted from the URL: URLs end up in proxy and
access logs.

### The bootstrap key

`ApiKey` (an environment variable or Docker secret) is the **bootstrap key**: full access, and the
only key first-run setup accepts. Once you have named keys and an admin account you can switch it
off with `Auth__BootstrapKeyEnabled=false`: then it's refused everywhere, only named keys and
signed-in sessions work, and **first-run setup can't run** (the startup log says so; see
[first run](#first-run-the-local-admin) for how to recover).

### Named keys

An admin manages them on the **API keys** page of the web UI (`/admin/api-keys`):

- **Create**: a name (what will use it), an access level and an optional expiry (30, 90 or 365
  days). The key, `sdp_<id>_<secret>`, is **shown once**, with a copy button. It is never stored:
  only a SHA-256 hash is kept, so a lost key can't be recovered; revoke it and create another.
- **Access**: **read-only** keys may only read (`GET`/`HEAD`), plus the event stream and the live
  hub; any change gets `403` with `"error": "read_only_key"`. **Full** keys can do everything the
  bootstrap key can, except first-run setup.
- **Revoke**: takes effect at once, without a restart. The next request with the key gets `401`,
  and anything it has open (an event stream, a hub connection) is ended. Expiry works the same way.
- **Last used** is shown per key. It is recorded at most once a minute per key and written in the
  background, never while answering a request.
- **Only an admin session can manage keys.** There is no key-management API: no key, not even the
  bootstrap key or a full named key, can create or revoke keys. A key that leaks can't be used to
  mint more.

Every failure (unknown, malformed, wrong, revoked or expired key) is the same `401`. A named key is
found by its id, then its hash compared in constant time; an unknown id still runs a comparison,
so timing doesn't say whether an id exists. Keys aren't hashed with a slow function like
passwords are: a key holds 256 random bits, so nobody can search that space even with the hash.

Keys live in `{Storage__BaseDirectory}/auth/api-keys.json` (written atomically, `schemaVersion` 1),
read once at startup. A file that can't be read fails closed: no named key is accepted and none can
be created or revoked until it is fixed or moved aside and the service restarted. The bootstrap key
still works. Creating and revoking keys is in the audit log (`StreamDeckPilot.Audit`) and published
as `ApiKeyCreated` / `ApiKeyRevoked` [live events](live-events.md); neither ever carries the key.

### Sessions on the API

A signed-in browser session can call the REST API too: an **admin** session fully, a **viewer**
session read-only (`403`, `"error": "read_only_session"`, for any change). Because the browser
sends the session cookie with any request to this site, changes made with a session must also send
the **`X-Requested-With`** header (any value; otherwise `403`, `"error": "csrf_header_required"`).
A page on another site can't add a custom header without a CORS preflight, which this app never
grants, so a forged form or script can't make the change. When the browser sends an `Origin`, it
must be the app's own (`403`, `"error": "cross_origin"`). Requests with an API key don't need the
header.

## Settings

| Variable | Meaning | Default |
|---|---|---|
| `Web__PublicOrigin` | The UI's public address, scheme and host only. Required with a provider. | unset |
| `Auth__Providers__N__…` | OpenID Connect providers, see above. | none |
| `Auth__Local__Enabled` | Local accounts: unset = on when no provider; `true`/`false`. | unset |
| `Auth__Local__MinimumPasswordLength` | Minimum local password length. | `12` |
| `Auth__SessionHours` | Session lifetime without activity (sliding). | `12` |
| `Auth__RevalidateMinutes` | How often a session is checked against the accounts (1–60). | `5` |
| `Auth__MaxSessionDays` | Longest a session lasts, however active (at least `SessionHours`, at most 90). | `7` |
| `Auth__AttemptLimit` | Sign-in (and setup) attempts per address per window. | `5` |
| `Auth__UserAttemptLimit` | Sign-in attempts per user name per window, from any address; `0` turns it off. | `10` |
| `Auth__AttemptWindowSeconds` | The rate-limit window. | `300` |
| `ApiKey` | The bootstrap API key; also what first-run setup asks for. | required for setup |
| `Auth__BootstrapKeyEnabled` | `false` refuses the bootstrap key everywhere (setup too); named keys and sessions still work. | `true` |
| `DataProtection__CertificatePath` | PFX that encrypts the Data Protection keys at rest. | unset (unencrypted) |
| `DataProtection__CertificatePassword` | Its password. | unset |

Bad settings (no way to sign in, a provider without `Web__PublicOrigin`, a malformed origin or
provider) stop the service at startup with a message naming the setting.
