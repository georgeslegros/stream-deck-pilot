#!/usr/bin/env bash
# Smoke checks for the app behind Traefik (see docker-compose.yml next to this file).
# Usage: smoke.sh [proxy-url]   (default http://localhost:8088)
# WS_PATH: when set (the CI job uses /_blazor, the web UI's circuit; later /hubs/live), also checks
# that a WebSocket upgrade on that path gets 101 through the proxy at both addresses.
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

  # The web UI (Web__Enabled=true) renders at the base, with a <base href> that carries it, so its
  # relative asset and circuit URLs resolve behind the proxy.
  expect "$label UI root" 200 "$(status "$host" "$base/")"
  local base_href
  base_href="$(curl -s -H "Host: $host" "$PROXY$base/" | grep -o '<base href="[^"]*"' | head -1 || true)"
  expect "$label UI base href" "<base href=\"$base/\"" "$base_href"

  if [[ -n "${WS_PATH:-}" ]]; then
    expect "$label WebSocket upgrade" 101 "$(status "$host" "$base$WS_PATH" --max-time 3 \
      -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' \
      -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' -H "X-Api-Key: $KEY" || true)"
  fi
}

check deck.example.test ""
check apps.example.test /deck

if [[ -z "${WS_PATH:-}" ]]; then
  echo "skip WebSocket upgrade: WS_PATH not set"
fi

if (( failures > 0 )); then echo "$failures check(s) failed"; exit 1; fi
echo "all reverse-proxy smoke checks passed"
