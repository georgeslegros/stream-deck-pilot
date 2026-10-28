#!/usr/bin/env bash
# Smoke checks for the app behind Traefik (see docker-compose.yml next to this file).
# Usage: smoke.sh [proxy-url]   (default http://localhost:8088)
# WS_PATH: space-separated paths that take the API key (the CI job uses "/hubs/live", the live
# events hub). For each, a WebSocket upgrade must get 101 through the proxy at both addresses. The
# web UI's circuit (/_blazor) needs a signed-in session (#97), so it is checked for a 401 instead.
set -euo pipefail

PROXY="${1:-http://localhost:8088}"
KEY="e2e-test-key"
failures=0

status() {   # status <host> <path> [extra curl args…] → HTTP status code
  local host="$1" path="$2"; shift 2
  curl -s -o /dev/null -w '%{http_code}' -H "Host: $host" "$@" "$PROXY$path"
}

expect() {   # expect <description> <expected> <actual>
  if [[ "$2" == "$3" ]]; then echo "ok   $1 ($3)"; else echo "FAIL $1: expected $2, got $3"; failures=$((failures + 1)); fi
}

wait_healthy() {
  local host="$1" path="$2"
  for _ in $(seq 1 60); do
    [[ "$(status "$host" "$path")" == "200" ]] && return 0
    sleep 1
  done
  echo "FAIL $host$path never became healthy"; exit 1
}

check() {   # check <host> <base>
  local host="$1" base="$2" label="$1$2"
  wait_healthy "$host" "$base/health"
  expect "$label health" 200 "$(status "$host" "$base/health")"
  expect "$label API without key" 401 "$(status "$host" "$base/devices")"
  expect "$label API with key" 200 "$(status "$host" "$base/devices" -H "X-Api-Key: $KEY")"
  expect "$label OpenAPI" 200 "$(status "$host" "$base/openapi.json")"

  # Forwarded host and path base reach the app: the OpenAPI server URL is the public address.
  local server
  server="$(curl -s -H "Host: $host" "$PROXY$base/openapi.json" | jq -r '.servers[0].url')"
  expect "$label OpenAPI server URL" "http://$host$base" "${server%/}"

  # The web UI (Web__Enabled=true) needs a signed-in user (#97): the root sends a browser to the
  # sign-in page under the same base. A fresh install (no account yet) goes on to first-run setup,
  # which renders with a <base href> that carries the base, so its relative URLs resolve.
  local location
  location="$(curl -s -o /dev/null -w '%{redirect_url}' -H "Host: $host" -H 'Accept: text/html' "$PROXY$base/")"
  expect "$label UI root redirects to sign-in" "http://$host$base/account/signin" "${location%%\?*}"
  expect "$label setup page" 200 "$(status "$host" "$base/account/setup")"
  local base_href
  base_href="$(curl -s -H "Host: $host" "$PROXY$base/account/setup" | grep -o '<base href="[^"]*"' | head -1 || true)"
  expect "$label UI base href" "<base href=\"$base/\"" "$base_href"

  local ws_headers=(-H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==')
  for ws in ${WS_PATH:-}; do
    expect "$label WebSocket upgrade $ws" 101 "$(status "$host" "$base$ws" --max-time 3 "${ws_headers[@]}" -H "X-Api-Key: $KEY" || true)"
  done
  # The circuit refuses a WebSocket without a session, and the hub one from another site's page.
  expect "$label circuit without a session" 401 "$(status "$host" "$base/_blazor" --max-time 3 "${ws_headers[@]}" || true)"
  expect "$label hub from another origin" 403 "$(status "$host" "$base/hubs/live" --max-time 3 "${ws_headers[@]}" \
    -H "X-Api-Key: $KEY" -H 'Origin: http://evil.example' || true)"

  # Live events (#108) stream through the proxy unbuffered: the snapshot arrives within 2 s, while
  # the response is still open (curl is cut off by --max-time, hence the || true).
  local first_event
  first_event="$(curl -sN --max-time 2 -H "Host: $host" -H "X-Api-Key: $KEY" "$PROXY$base/api/events" \
    | grep -m1 '^event: ' || true)"
  expect "$label live events stream" "event: snapshot" "$first_event"
}

check deck.example.test ""
check apps.example.test /deck

if [[ -z "${WS_PATH:-}" ]]; then
  echo "skip WebSocket upgrade: WS_PATH not set"
fi

if (( failures > 0 )); then echo "$failures check(s) failed"; exit 1; fi
echo "all reverse-proxy smoke checks passed"
