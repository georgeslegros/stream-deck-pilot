# Running behind a reverse proxy

The service listens on plain HTTP (port `8080` in the container). Put a reverse proxy in front of it
for HTTPS and a host name: Traefik, nginx and Caddy all work, and the examples below can be copied
as they are. Replace `deck.example.com` with your own host name.

What the app does for you:

- **Forwarded headers.** `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` are honoured
  when the request comes from a trusted proxy, so the app sees the client's address, `https` and the
  public host name. Generated links (the OpenAPI document's `servers` entry, and later the web UI and
  sign-in redirects) use them. Headers from any other peer are ignored.
- **Path base.** Without a dedicated host name, serve the app under a sub-path such as `/deck`
  (`Proxy__PathBase=/deck`). The REST API, `/health`, `/openapi.json`, static files, the web UI and
  its live connection all live under that path.
- **WebSockets.** Enabled for the web UI and the live hub (SignalR). The proxy must pass the
  `Upgrade` header through; the examples below do. With a single instance no sticky sessions are
  needed.
- **Health checks.** `GET /health` needs no API key, so the proxy can use it.

## Settings

| Variable | Description | Default |
|---|---|---|
| `Proxy__TrustedNetworks__0`, `__1`, … | CIDR ranges allowed to set forwarded headers. Setting any entry replaces the whole default list. | `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `127.0.0.0/8`, `fc00::/7`, `::1/128` |
| `Proxy__TrustedProxies__0`, `__1`, … | Single proxy addresses trusted in addition to the networks. | none |
| `Proxy__ForwardLimit` | Proxy hops to unwind in `X-Forwarded-For`. Keep `1` for one proxy; raise it only for a chain of trusted proxies (e.g. a CDN in front of Traefik). | `1` |
| `Proxy__PathBase` | Sub-path the app is served under, e.g. `/deck`: starts with `/`, no trailing `/`. | none (root) |
| `AllowedHosts` | Host names the app answers to, separated by `;`. A forwarded host outside the list is ignored. | `*` |

The default trusted networks cover a proxy in the same Docker network, on the same host, or on the
LAN. If the app's port is also reachable directly from other machines on the LAN, those machines
could set forwarded headers too: narrow `Proxy__TrustedNetworks__0` to the proxy's network (for
example the Docker network's subnet) or list the proxy in `Proxy__TrustedProxies__0`, and don't
publish the port on the host at all when the proxy runs in Docker.

An invalid entry (not a CIDR range or IP address, or a malformed path base) stops the app at
startup with the offending value in the error.

### Path base

With `Proxy__PathBase=/deck`:

- `https://apps.example.com/deck/devices`, `/deck/health`, `/deck/openapi.json`… are served.
- The proxy must pass the path **unchanged**: don't strip the prefix (no Traefik `StripPrefix`
  middleware, no URI part on nginx's `proxy_pass`, `handle` rather than `handle_path` in Caddy).
- Anything outside the base answers `404`, except `GET /health`, which stays at the root too for
  container and orchestrator health checks that talk to the app directly.

## Traefik (Docker labels)

```yaml
services:
  stream-deck-pilot:
    image: ghcr.io/georgeslegros/stream-deck-pilot:latest
    environment:
      - ApiKey=${API_KEY:?API_KEY is required}
      # - Proxy__PathBase=/deck          # only when serving under a sub-path
    networks: [traefik]
    # No "ports:" — only Traefik reaches the app.
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.streamdeck.rule=Host(`deck.example.com`)"
      # Sub-path instead: Host(`apps.example.com`) && PathPrefix(`/deck`)
      - "traefik.http.routers.streamdeck.entrypoints=websecure"
      - "traefik.http.routers.streamdeck.tls.certresolver=letsencrypt"
      - "traefik.http.services.streamdeck.loadbalancer.server.port=8080"
      - "traefik.http.services.streamdeck.loadbalancer.healthcheck.path=/health"
      - "traefik.http.services.streamdeck.loadbalancer.healthcheck.interval=30s"

networks:
  traefik:
    external: true
```

Traefik sets the `X-Forwarded-*` headers and passes WebSocket upgrades without extra
configuration. [`docker-compose.traefik.example.yml`](../docker-compose.traefik.example.yml) is a
ready-made overlay for the repository's `docker-compose.yml`.

## nginx

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    server_name deck.example.com;
    # ssl_certificate / ssl_certificate_key …

    location / {                       # sub-path instead: location /deck/ { … }
        proxy_pass http://127.0.0.1:8080;   # no URI part: the path is passed unchanged
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_read_timeout 1h;           # keep idle WebSockets open
    }
}
```

When nginx runs on the host and the app in Docker with `-p 127.0.0.1:8080:8080`, the app sees the
Docker gateway (a `172.16.0.0/12` address) as the peer, which the defaults trust.

## Caddy

```caddyfile
deck.example.com {
    reverse_proxy 127.0.0.1:8080
}

# Sub-path instead (handle, not handle_path, so /deck is kept):
# apps.example.com {
#     handle /deck* {
#         reverse_proxy 127.0.0.1:8080
#     }
# }
```

Caddy obtains the certificate, sets the `X-Forwarded-*` headers and passes WebSocket upgrades on
its own.

## Keep it private

The REST API is protected by the API key, but the service controls physical devices and, until
sign-in arrives, the web UI has no login of its own. Expose it on your LAN or over a VPN
(WireGuard, Tailscale…) rather than to the internet. If it must be reachable from outside, add
authentication at the proxy (e.g. Traefik `forwardAuth`, an OAuth2 proxy, or Caddy `basic_auth`)
and keep `ApiKey` long and secret.

## Tested in CI

The `Reverse proxy (E2E)` job builds the image and runs it, with the web UI enabled, behind a
Traefik container twice: at the root of a host name and under `/deck`. It checks health, the API
key, the OpenAPI document and the forwarded host and path base, that the UI renders with the
right `<base href>`, and that a WebSocket upgrade of the UI's circuit (`/_blazor`) gets `101`
through the proxy (`e2e/reverse-proxy/`).
