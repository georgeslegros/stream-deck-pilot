# Stream Deck Pilot — API Guide

This service is designed to be driven by Claude Code or scripts. All endpoints accept and return JSON. The OpenAPI schema is always available at `GET /openapi.json` (no auth required).

---

## Authentication

Every endpoint except `/health` and `/openapi.json` requires:

```
X-Api-Key: <value of the API_KEY environment variable>
```

Missing or wrong key → `401 Unauthorized`.

---

## Mental model

```
Device catalogue (app-owned, read-only via API)
  └─ DeviceEntry  serial, model, key geometry, first/last seen
        └─ DeviceConfig  (one per serial, API-managed)
              └─ Page[]  (type discriminator: "ButtonGrid")
                    └─ ButtonDefinition[]
                          ├─ Display      icon, static label, format template
                          ├─ Inbound      MQTT topic, field extraction, staleness
                          ├─ Rules[]      ordered conditions → colour/icon override
                          └─ Gestures     "Tap" → [PublishAction | NavigateAction]
```

**Key rules:**
- You cannot write config for a serial that isn't in the catalogue. The device must have been physically connected at least once.
- Key positions (`keyIndex`) are 0-based, left-to-right, top-to-bottom. MK.2 = 0–14 (5 cols × 3 rows).
- All config writes are validated before persisting. Errors return `400` with an `errors` array.

---

## Endpoints

### `GET /devices`
List the device catalogue with current connection state.

```jsonc
// Response 200
[
  {
    "serial": "A7FZA5191LB60S",
    "model": "Stream Deck MK.2 (Scissor Switch)",
    "keyRows": 3,
    "keyColumns": 5,
    "firstSeen": "2026-06-05T10:00:00Z",
    "lastSeen": "2026-06-05T12:00:00Z",
    "connectionState": "Connected"   // Unknown | Disconnected | Connecting | Connected | Faulted
  }
]
```

### `GET /devices/{serial}/status`
Connection state for one device. `404` if serial not in catalogue.

### `GET /devices/{serial}/config`
Current persisted config. `404` if no config written yet.

### `PUT /devices/{serial}/config`
Write (replace) the full config for a device. Validated before save.

```jsonc
// Request body — DeviceConfig
{
  "schemaVersion": 1,
  "serial": "A7FZA5191LB60S",
  "pages": [
    {
      "pageType": "ButtonGrid",
      "pageId": "main",
      "buttons": [ /* ButtonDefinition[] — see below */ ]
    }
  ]
}
// Response 204 No Content on success
// Response 400 { "errors": ["..."] } on validation failure
```

### `POST /config/upgrade`
Migrate a config JSON from any supported schema version to the current version. Does **not** persist.

```jsonc
// Request: any DeviceConfig JSON (any supported schemaVersion)
// Response 200: same config at current schemaVersion
// Response 422: { "error": "unsupported_schema_version", "message": "..." }
```

### `POST /devices/{serial}/force-render`
Re-renders all keys from desired state. Useful after manually editing config on disk.

### `POST /devices/{serial}/images`
Upload a custom icon (PNG or JPEG, max 512 KB). `multipart/form-data`.

```
Response 200: { "ref": "custom:my-icon.png" }
```

Use `"ref"` as `display.baseIcon` in a `ButtonDefinition`.

### `GET /devices/{serial}/images` / `DELETE /devices/{serial}/images/{filename}`
List or remove custom icons.

### `GET /openapi.json`
Full OpenAPI 3 schema (no auth required).

---

## ButtonDefinition schema

```jsonc
{
  "buttonId": "office-co2",        // stable human-readable ID
  "keyIndex": 0,                   // 0-based, within page
  "pageId": "main",

  "display": {
    "baseIcon": "builtin:co2",     // "builtin:<name>" or "custom:<filename>" or null
    "staticLabel": "CO₂",
    "formatTemplate": "{value} {unit}"  // tokens: {value} {unit} {label}
  },

  "inbound": {                     // null for press-only buttons
    "topic": "home/office/co2",
    "valueField": "value",         // JSON path-lite: "value" or "sensor.value"
    "unitField": "unit",
    "expectsRetained": true,       // shows placeholder until first value arrives
    "stalenessTimeout": "00:00:30" // TimeSpan; null = never goes stale
  },

  "rules": [                       // ordered, first-match-wins, over extracted numeric value
    { "condition": ">1000", "backgroundColour": "#FF0000", "icon": null },
    { "condition": ">800",  "backgroundColour": "#FF8800", "icon": null },
    { "condition": ">=0",   "backgroundColour": "#00AA00", "icon": null }
  ],
  // condition grammar: ">N" ">=N" "<N" "<=N" "==N" "between:A:B"

  "gestures": {
    "Tap": [
      { "type": "Publish", "topic": "home/ventilation/toggle", "payload": "true" },
      { "type": "Navigate", "targetPageId": "menu" }
      // actions execute in order, fire-and-forget
    ]
  }
}
```

---

## Built-in icons

Reference as `"builtin:<name>"`:

| Name | Colour | Symbol |
|------|--------|--------|
| `thermometer` | red | T° |
| `humidity` | blue | H% |
| `co2` | green | CO₂ |
| `power` | yellow | ⚡ |
| `home` | purple | ⌂ |
| `arrow-left` | teal | ◄ |
| `arrow-right` | teal | ► |
| `fallback` | dark grey | ? |
| `placeholder` | very dark | … |

---

## MQTT conventions

**Inbound (sensor → button):**
Preferred payload shape: `{"value": 1023, "unit": "ppm", "ts": "2026-06-05T12:00:00Z"}`.
Bare numeric strings also work (`"1023"`), but self-describing JSON is preferred.
Publishers should set the **retain** flag so the app populates immediately on start.

**Outbound (button press → broker):**
`PublishAction.payload` is emitted verbatim. Choose a stable payload per button (e.g. `"true"`, `"toggle"`, or a JSON string).

---

## Worked example: configure a CO₂ monitor button

```bash
# 1. Discover what's connected
curl -H "X-Api-Key: $API_KEY" http://streamdeck.local/devices

# 2. Write a config with one CO₂ button on key 0 and a nav button on key 14
curl -X PUT \
  -H "X-Api-Key: $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "schemaVersion": 1,
    "serial": "A7FZA5191LB60S",
    "pages": [{
      "pageType": "ButtonGrid",
      "pageId": "main",
      "buttons": [
        {
          "buttonId": "co2",
          "keyIndex": 0,
          "pageId": "main",
          "display": { "baseIcon": "builtin:co2", "staticLabel": "CO2", "formatTemplate": "{value} {unit}" },
          "inbound": { "topic": "home/office/co2", "valueField": "value", "unitField": "unit", "expectsRetained": true, "stalenessTimeout": "00:01:00" },
          "rules": [
            { "condition": ">1000", "backgroundColour": "#FF0000", "icon": null },
            { "condition": ">=0",   "backgroundColour": "#00AA00", "icon": null }
          ],
          "gestures": {}
        }
      ]
    }]
  }' \
  http://streamdeck.local/devices/A7FZA5191LB60S/config

# 3. Force a re-render immediately
curl -X POST -H "X-Api-Key: $API_KEY" \
  http://streamdeck.local/devices/A7FZA5191LB60S/force-render

# 4. Verify MQTT is updating the button — publish a test value
mosquitto_pub -h rabbitmq.local -u $MQTT_USER -P $MQTT_PASS \
  -r -t home/office/co2 -m '{"value":850,"unit":"ppm"}'
```

---

## Error reference

| Code | Meaning |
|------|---------|
| 401 | Missing or wrong `X-Api-Key` |
| 400 | Validation error — `errors` array in body |
| 404 | Serial not in catalogue, or no config for that serial |
| 422 | Schema version too old for `POST /config/upgrade` |
| 204 | Config saved successfully (`PUT /devices/.../config`) |
