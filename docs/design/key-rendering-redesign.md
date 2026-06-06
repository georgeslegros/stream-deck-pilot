# Stream Deck Key Rendering — Design Improvement Report

Target: Elgato Stream Deck MK.2 — 15 keys, each a 72×72 px backlit LCD (~20×20 mm),
viewed at ~50 cm with ~8 mm inter-key gaps (each key reads as a discrete unit, not a grid cell).

Renderer: `src/StreamDeckPilot.Infrastructure/Rendering/KeyBitmapComposer.cs`
Icon source: `src/StreamDeckPilot.Infrastructure/Icons/GeneratedIconSource.cs`
Model: `src/StreamDeckPilot.Core/Models/Config/DisplaySpec.cs`

---

## 0. Root-cause summary of current problems

| Symptom | Cause in code |
|---|---|
| Icon is a small opaque square covering the tile colour | `GeneratedIconSource.Generate()` fills the whole 72×72 PNG with `def.Bg` before drawing the glyph. The composer then shrinks that *filled* square to 44 px and pastes it at (6,6). Two backgrounds fight. |
| Value text cramped at bottom, hard to scan | Label is 11 pt, white, single baseline at `y = Size-15`. The numeric value (the actual payload) is treated as a footnote. |
| Invisible on yellow / dark tiles | `Color.White` is hardcoded for both glyph and label regardless of background luminance. |
| Three temperature tiles look identical | All bind `builtin:thermometer`; differentiation depends on a label nobody can read at 50 cm. |
| Top-left composition feels unbalanced | Icon pinned at (6,6); empty L-shaped gutter on right and bottom. |

The fix is a coordinated change across all three files: make icons **transparent glyphs**, introduce **two distinct layouts** (sensor vs. toggle), and make **all foreground colour adaptive**.

---

## 1. Layout & composition

Drive layout from the tile's *role*, not a single code path. Derive role from config:
`isSensor = FormatTemplate != null` (tile shows a live numeric value);
otherwise it is a `toggle/label` tile (room name, navigation).

### Layout A — Sensor tile (CO₂, temperatures, humidity)
The **value is primary**. Stack: small glyph top, big value centred, optional unit.

```
 ┌──────────────┐  y
 │   ◆ glyph    │  4–26   glyph band, 22 px tall, centred horizontally
 │              │
 │   23.5       │  30–58  VALUE — 24 px bold, centred (the hero)
 │    °C        │  58–70  unit — 11 px, centred, 70% alpha
 └──────────────┘
```
The value occupies the optical centre and reads from across the room. The glyph is a
quiet "what am I" cue above it; the unit is a quiet "what scale" cue below.

### Layout B — Toggle / light tile (Bureau, Aspirer, Parking…)
The **state is primary** (on/off conveyed by the *tile background*, see §4), the **icon is the hero**, the room name is the caption.

```
 ┌──────────────┐  y
 │  ┌────────┐  │  6–48   GLYPH — 40 px, centred horizontally, optical-centre vertical
 │  │ glyph  │  │
 │  └────────┘  │
 │   Bureau     │  54–70  caption — 12 px, centred
 └──────────────┘
```

### Layout C — Icon-only tile (navigation arrows, scene buttons, no label)
Full-bleed centred glyph, **56 px**, centred on the full canvas at (36,36). No label band.

### Layout D — Empty / unassigned key
Leave black (current behaviour). Do **not** render a fallback "?" tile for *unconfigured*
positions — reserve the `?` glyph strictly for *configured-but-broken* icon references.

**Canvas budget (all layouts):** keep a 4 px safe margin (was 6 — too generous at this size;
the inter-key gap already separates tiles physically, so internal margin can shrink and give
glyphs/text more room).

---

## 2. Icon sizing & positioning

Switch from "opaque square pasted top-left" to "transparent glyph composited over the tile".

- **Generate icons with a transparent background.** In `GeneratedIconSource.Generate`,
  drop `ctx.Fill(bg)`. The PNG becomes alpha-only: just the glyph in a foreground colour
  (ideally render the glyph **white**, then tint at compose time — see §4). The tile
  background then shows through everywhere the glyph isn't.
- **Sizes by layout:**
  - Sensor glyph (Layout A): **22 px**, top band, centred at x=36, baseline ~24.
  - Toggle glyph (Layout B): **40 px**, centred at x=36, vertical centre ~26.
  - Icon-only (Layout C): **56 px**, centred at (36,36).
- **Always centre horizontally** (`x = (72 - iconSize)/2`), never pin to the left margin.
  Top-left placement is the single biggest contributor to the "unbalanced/cluttered" look.
- **Resize quality:** when scaling the 72 px source glyph down, use a high-quality resampler
  (`Resize(new ResizeOptions{ Size = ..., Sampler = KnownResamplers.Lanczos3 })`) so thin
  glyph strokes stay crisp on the LCD rather than turning to mush.

---

## 3. Text readability

The current 11 pt white footnote is the weakest part. Re-tier typography:

| Element | Layout | Size (px) | Weight | Position | Alpha |
|---|---|---|---|---|---|
| Sensor **value** (`23.5`) | A | **24** | Bold | centred, optical-centre y≈42 | 100% |
| Sensor **unit** (`°C`, `ppm`, `%`) | A | 11 | Regular | centred, y≈64 | 70% |
| Toggle **caption** (room) | B | 12 | Regular/Medium | centred, y≈62 | 100% |
| Icon-only | C | — | — | none | — |

Notes:
- **Split value from unit.** Right now `FormatTemplate` produces e.g. `"23.5 C"` as one
  string drawn small. Render the numeric part large and the unit small. If the model isn't
  changed, a cheap heuristic: split the formatted string on the first space — the head is the
  value (big), the tail is the unit (small). Cleaner long-term: add a `Unit` field to
  `DisplaySpec` (see §7) so the template emits only the number.
- **Bold the value.** Load a second font instance at the larger size with bold if available
  (`SystemFonts.Get("DejaVu Sans").CreateFont(24, FontStyle.Bold)`); DejaVu Sans Bold ships
  with the package the Linux image already pulls.
- **One line only** (matches the no-wrap constraint). For overflow keep the existing
  `…` truncation but raise the cutoff: at 12 px a room caption fits ~8–9 chars, not 12.
- **Add a subtle text shadow/outline** for the value: draw the glyph string once in
  black at +1px offset (or a 1px black outline) *under* the adaptive-colour text. This keeps
  the value legible even when the background colour is mid-luminance and ambiguous — far more
  robust than relying on the luminance switch alone.

---

## 4. Adaptive foreground colour (the contrast fix)

Replace every hardcoded `Color.White` with a computed foreground based on background luminance.

**Formula (sRGB relative luminance, WCAG):**
```
L = 0.2126*R + 0.7152*G + 0.0722*B      // R,G,B in 0..1 linearised, or the cheap
                                         // perceptual approximation below
```
Cheap, good-enough integer version (no linearisation needed at this scale):
```csharp
double L = (0.299*r + 0.587*g + 0.114*b) / 255.0;   // r,g,b 0..255
Color fg = L > 0.6 ? near-black : near-white;
```
Use **near-black `#1A1A1A`** and **near-white `#F2F2F2`** rather than pure 0/255 — pure black
on a saturated backlit LCD haloes; near-tones look cleaner.

- Threshold **0.6** puts `#FFD700` yellow (L≈0.84) into the dark-text bucket (fixes the
  yellow-light tiles) and dark grey `#444` (L≈0.27) into white-text. Verify the project's
  actual palette against the threshold once implemented.
- Apply the **same** computed `fg` to the glyph tint. Because §2 makes glyphs transparent
  white masks, tint = recolour the non-transparent pixels to `fg` (multiply, or
  draw the glyph string directly in `fg` at compose time instead of pre-baking white).
- **Toggle OFF state:** instead of a near-black tile that swallows everything (current
  vacuum/parking problem), use a **dark-but-not-black** background `#2A2A2A` *plus* a desaturated
  glyph, and reserve true black only for *unconfigured* keys. This keeps "off-but-present"
  visually distinct from "no button here".

---

## 5. Visual differentiation for same-type sensors

Three thermometers must be distinguishable pre-reading. Layered cues, cheapest first:

1. **Background as the primary differentiator (best ROI).** Map each *zone* to a stable hue:
   office=amber, salon=teal, playroom=violet. The viewer learns "amber tile = office"
   positionally and chromatically without reading. This is config-level, not code: set
   distinct `BackgroundColour` per tile. Recommend documenting a per-zone palette.
2. **Value-driven background (even better for temperature).** Use the existing conditional-rule
   engine to colour by the *reading*: cold→blue, comfortable→green, hot→red. Now the three
   tiles differ by what they're actually telling you, which is the point of a glanceable display.
3. **Accent corner chip.** Draw a small 10×10 px rounded square in the top-right corner in a
   per-zone accent colour — a "tab" identifying the source while the value stays the focus.
4. **Distinct glyph variants.** Add `thermometer-room`, `thermometer-outdoor`, etc. with subtly
   different symbols (house outline vs. tree vs. sofa). Lowest ROI at 22 px — geometry barely
   reads — so prefer 1–3.

Recommendation: ship #1 + #2 immediately (config + existing rule engine, zero new code),
add #3 if differentiation is still weak.

---

## 6. Icon transparency & symbol vocabulary

- **Transparency win:** transparent glyphs let the (adaptive, meaningful) tile colour carry
  state and identity, while the glyph carries category. One canvas, two channels — instead of
  today's two fighting opaque rectangles. It also makes the §4 tinting trivial.
- **Symbol vocabulary that survives 22–56 px on a backlit LCD** (favour bold, closed,
  high-fill shapes; avoid thin multi-stroke line art and most emoji, which render as flat
  low-contrast or inconsistent glyphs cross-platform):
  - Temperature → filled thermometer silhouette, or simply a bold `°` is unnecessary since the
    value already shows degrees; prefer a thermometer bulb shape.
  - Humidity → solid water droplet.
  - CO₂ → keep the `CO₂` lettering (text *is* the clearest symbol here at this size).
  - Light/power → filled lightning bolt or a filled bulb; keep `⚡` but render bold & large.
  - Vacuum → robot/disc silhouette rather than the `⌂` house (house reads as "home").
  - Navigation → bold solid triangles `►`/`◄` (already good).
- **Pre-rasterise real icons.** Pure ImageSharp can't render SVG, but you can ship a small set
  of **pre-rasterised PNG glyphs** (white-on-transparent, 72×72, from e.g. Material Symbols /
  Lucide exported to PNG) under the built-in icon set, indexed by the same `builtin:` names.
  This replaces the font-glyph hack with clean, consistent artwork and keeps the renderer
  unchanged (still composites a transparent PNG). Tint at compose time per §4.

---

## 7. Suggested model / code touch-points

Minimal-to-clean ordering:

1. **`GeneratedIconSource.Generate`** — remove `ctx.Fill(bg)`; render glyph in white on
   transparent. (Unblocks §2/§4/§6 with one line.)
2. **`KeyBitmapComposer.Compose`** — branch into Layout A/B/C by role; centre icons; compute
   adaptive `fg`; tint glyph + text with `fg`; add value-text outline; high-quality resampler.
3. **`DisplaySpec`** — add optional `Unit` (string) and `Layout` (enum: `Auto|Sensor|Toggle|Icon`)
   so layout is declarative rather than inferred. `Auto` preserves the inference heuristic.
4. Per-zone palette + value→colour rules: **config only**, no code (documented palette).

### Concrete constants to land in `KeyBitmapComposer`
```
Size           = 72
SafeMargin     = 4
GlyphSensor    = 22   // top band, centred x, baseline ~24
GlyphToggle    = 40   // centred x, vertical centre ~26
GlyphIconOnly  = 56   // centred (36,36)
ValueFontPx    = 24   // bold
UnitFontPx     = 11   // 70% alpha
CaptionFontPx  = 12
LumaThreshold  = 0.60
FgLight        = #F2F2F2
FgDark         = #1A1A1A
ToggleOffBg    = #2A2A2A   // distinct from unconfigured black
```

---

## 8. Before / after, per the photographed keys

| Key | Now | After |
|---|---|---|
| CO₂ 1052 ppm | tiny green square + footnote | red tile (rule: high CO₂), `CO₂` glyph top, **1052** big centred, `ppm` small |
| Office temp 23.5 | red square top-left, tiny label | amber/green tile, thermometer glyph, **23.5** big, `°C` small |
| 3× thermometers | identical | three zone hues (or value-driven colour) + big values — instantly distinct |
| Bureau light ON | yellow tile, white-on-yellow (low contrast) | yellow tile, **dark** glyph+caption (adaptive), 40 px bolt |
| Aspirer / Parking OFF | near-invisible on black | `#2A2A2A` tile, light glyph+caption, clearly "present but off" |
| Empty keys | black | unchanged (true black = unconfigured) |
