# StreamDeckPreview

A headless tool that renders a device config into a Stream-Deck-style **board PNG**, using
the *same* `KeyBitmapComposer` the service uses to drive the hardware. The output is therefore
pixel-accurate to what a real device shows — no GUI, no physical deck required. Handy for
documentation images and for eyeballing layout/sizing changes.

## Usage

```bash
dotnet run --project tools/StreamDeckPreview -- <config.json> [outDir] [sampleData.json] [cols] [rows]
```

- `config.json` — a `DeviceConfig` JSON (any supported schemaVersion; v1 is migrated to current on load).
- `outDir` — where to write `board-<pageId>.png` (one per page). Defaults to the current directory.
- `sampleData.json` — optional live-data overlay so templates resolve and rules fire:
  ```json
  { "<buttonId>": { "value": "850", "unit": "ppm", "label": "22/18" }, ... }
  ```
  A button with no entry renders in its no-data state (zones fall back to their static labels).
- `cols` / `rows` — board geometry. Defaults to `5 3` (Stream Deck MK.2).

## Regenerating the documentation images

The boards embedded in `docs/` are generated from the committed sample under `samples/`:

```bash
dotnet run --project tools/StreamDeckPreview -- \
  tools/StreamDeckPreview/samples/A7FZA5191LB60S.json \
  docs/images \
  tools/StreamDeckPreview/samples/A7FZA5191LB60S.sample.json
```

This writes `docs/images/board-main.png` and `docs/images/board-climate.png`.

> The sample config under `samples/` is a reconstruction of the live homelab layout
> (`docs/live-device-config.md`) with representative readings — it is for previews/docs,
> not the source of truth.
