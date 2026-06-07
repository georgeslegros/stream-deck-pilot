# Stream Deck Pilot — Icon Vocabulary Recommendation

> **Status (what actually shipped — read this first).** This document is the
> *pre-implementation design exploration*. The renderer took a different (simpler)
> path than its primary recommendation, so treat §1 and the §6 "proposed builtin:
> names" table as historical rationale, **not** as the current contract:
>
> - Icons are resolved from the **full Material Design Icons webfont** embedded in the
>   app (`materialdesignicons-webfont.ttf` + `mdi-meta.json`). `MdiIconSource` renders
>   **any** MDI glyph by its real name at runtime — there is **no** build-time PNG step
>   and **no** custom rename layer.
> - Reference icons as **`builtin:<mdi-name>`** using the actual MDI name, e.g.
>   `builtin:thermometer`, `builtin:molecule-co2`, `builtin:water-percent`,
>   `builtin:lightbulb`, `builtin:lightbulb-outline`, `builtin:robot-vacuum`,
>   `builtin:shield-check`, `builtin:chevron-right`. Browse names at
>   <https://pictogrammers.com/library/mdi/>. (Do **not** use the invented
>   `builtin:co2`/`builtin:light-on` names from §6 — those were never implemented.)
> - Glyphs are drawn transparent and **tinted to an auto-chosen ink colour** for
>   contrast (the adaptive-luminance idea in §3 — that part did ship).
> - State-driven icon swaps (§4) shipped: a conditional rule can carry an `icon`.
> - A handful of legacy generated tokens (`co2`, `thermometer`, `placeholder`,
>   `fallback`, …) remain only as a fallback when an MDI name isn't found.
>
> The symbol *choices* in §2/§4/§5 and the icon-set survey in §6 remain useful as a
> vocabulary guide. For the authoritative config/icon contract, see `api-guide.md`.

---

Target hardware: Elgato Stream Deck MK.2, 72×72 px LCD keys, ~20×20 mm, ~50 cm
viewing distance. Effective glyph budget after the composer's 6 px margin and
16 px reserved label strip: **icon draws into ~44–60 px**. At 50 cm a 20 mm key
subtends ~2.3°; a recognisable pictogram needs its primary stroke to be
**≥ 3 px wide** and its silhouette to fill **≥ 70 %** of the icon box. Text
labels longer than 3 glyphs are illegible — symbols must carry meaning.

Current renderer (verified):
- `GeneratedIconSource.Generate()` — fills a solid `Rgba32` background, draws
  white centred text at 18 px. No transparency (full-bleed fill).
- `KeyBitmapComposer.Compose()` — fills tile bg, resizes the icon PNG to
  `iconSize = 72 - 12 - 16 = 44 px` (60 px when no label), composites at
  `(6,6)` with `DrawImage(..., 1f)`, then draws an 11 px white bottom label.
- `IconResolver` — references are `builtin:<name>` or `custom:<file>`; unknown →
  grey fallback.

Because the generated icon is itself a *solid square*, the composer paints a
44 px coloured square on top of the tile background — the symbol never blends
with the tile. **This is the root cause of "small coloured squares with text".**

---

## 1. Icon format recommendation

### Decision: embed a small set of **pre-rasterised transparent PNGs** as
assembly resources, generated offline from an MIT/Apache SVG set at **3 sizes
(44, 60, 88 px)**, white-on-transparent. Keep the generated-raster path only as
the text fallback. Do **not** rasterise SVG at runtime.

| Option | Quality @44–60 px | Maintenance | Transparency | Verdict |
|---|---|---|---|---|
| **A. Generated raster (current, improved)** | Poor for true symbols — ImageSharp text rendering of Unicode pictographs depends on the system font's glyph coverage, which under Linux/Docker is unreliable (`⌂`, `⚡`, `⏻` may be missing/boxed). Fine for **text tokens** ("CO₂", "44%"). | Trivial — code only. | Achievable: draw on transparent canvas instead of `ctx.Fill(bg)`. | Keep as **fallback + numeric-value tiles** only. |
| **B. Embedded pre-rasterised PNG resources** | **Best.** Designer-controlled hinting; pixel-snapped strokes; deterministic across OS. Bundle 88 px masters, downscale with `KnownResamplers.Lanczos3`. | Low — drop files in `Resources/Icons/`, reference by name. No new NuGet dep. | Native (RGBA PNG, white pixels + alpha). | **Recommended primary.** |
| **C. Runtime Unicode/emoji via bundled font** | Risky. Monochrome symbol glyphs (e.g. from a bundled `Material Symbols` or `Noto Sans Symbols2` TTF) render fine; **colour emoji (COLR/CBDT) are NOT supported by SixLabors.Fonts** — they render as tofu or flat outlines. Glyph centring/baseline at 44 px needs per-glyph tuning. | Medium — must bundle and license a font, map names→codepoints. | Yes (transparent canvas). | Use only for the **text fallback font**; not for pictograms. |

> **ImageSharp + SVG:** ImageSharp has **no SVG rasteriser**. Runtime SVG would
> require `Svg.Skia` + `SkiaSharp` (extra native `libSkiaSharp.so` in the Linux
> image) or `Svg` (GDI+, not Linux-safe). Not worth the deploy complexity for a
> fixed ~12-icon vocabulary — **pre-rasterise at build time instead.**

### Build-time pipeline
1. Pick icons from one MIT/Apache set (see §6).
2. Render each SVG to white-on-transparent PNG at 88 px (e.g. `resvg`/Inkscape
   in CI, or one-off locally).
3. Drop into `src/StreamDeckPilot.Infrastructure/Resources/Icons/<name>.png`,
   mark `<EmbeddedResource>`.
4. Extend `IconResolver` with a `builtin:` lookup that reads the embedded
   resource, recolours per §3, downscales with Lanczos3 to `iconSize`.

---

## 2. Symbol choices per button type

White monochrome glyph, transparent background, tile colour shows through.
"MDI" = Material Design Icons name. Unicode codepoint given where a bundled
symbol font could substitute acceptably; "custom asset" where Unicode is
inadequate at 44 px.

| Button | Recommended symbol | Asset | Notes |
|---|---|---|---|
| **CO₂** | Filled **cloud** (`weather-cloudy` / MDI `cloud`) with bold "CO₂" as the *tile label* below, not inside the icon. Alt: `molecule-co2` (MDI has it). | **Custom asset** — `molecule-co2` (MDI). Unicode `☁ U+2601` is too thin/whitespace-y at 44 px. | Drop the green square; green stays as tile bg. Reading "cloud + CO₂ label" is unambiguous. |
| **Temperature** | Classic **thermometer** (MDI `thermometer`). Bulb + stem silhouette reads at 44 px. | **Custom asset.** No reliable Unicode thermometer (`🌡 U+1F321` is colour-emoji → unsupported). | Differentiate rooms via §5. |
| **Humidity** | **Water drop** (MDI `water` / `water-percent`). Universally read. | **Custom asset.** `💧 U+1F4A7` is emoji (unsupported); `🌢 U+1F322` deprecated. | Keep blue bg. `water-percent` even embeds the % — strong choice. |
| **Light toggle** | **Lightbulb**: ON = filled `lightbulb` (MDI `lightbulb`/`lightbulb-on`), OFF = outline `lightbulb-outline`. | **Custom asset (2 variants).** Avoid `⚡`/`⏻` — lightning means "energy/flash", not "light". Power symbol `⏻ U+23FB` is OK as a *generic toggle* but ambiguous for lighting. | See §4 for state. |
| **Vacuum** | **Robot vacuum**: MDI `robot-vacuum` (running) and `robot-vacuum` + dock, or MDI `robot-vacuum-variant`. Distinct from house. | **Custom asset.** No Unicode robot-vacuum. `🤖`/`🏠` are emoji. | Stop reusing `⌂` (home). See §4. |
| **Pi-hole / DNS** | **Shield**: MDI `shield-check` (enabled/blocking) vs `shield-off`/`shield-alert` (disabled). Alt: `dns` icon. | **Custom asset.** No good Unicode shield at 44 px (`🛡 U+1F6E1` emoji). | Must differ from light `⚡`. Shield = "protection/blocking" reads correctly for ad/DNS blocking. |
| **Navigation** | Chevrons: `chevron-left`/`chevron-right` (MDI). | Custom asset, or Unicode `◄ U+25C4` / `► U+25BA` (geometric shapes — **well-supported**, acceptable). | Current arrows are fine; the geometric-shapes block is reliably present in DejaVu/Liberation. |

**Unicode-adequate (keep generated path):** geometric arrows `◄ ► ▲ ▼`,
fallback `?`, ellipsis `…`, and **numeric value tiles** (temperature reading,
"44%", "612 ppm") where the *number is the icon*.

---

## 3. Transparency and colour adaptation (precise rule)

Render every embedded icon as **white (#FFFFFF) pixels with an alpha channel**
(the master is white-on-transparent). At composite time, recolour the glyph to
maximise contrast against the tile background:

1. Parse tile background `(R,G,B)` in 0–255 (already done in
   `KeyBitmapComposer.ParseColour`).
2. Convert each channel to linear and compute **relative luminance** (WCAG):

   ```
   f(c) = (c/255 <= 0.03928) ? (c/255)/12.92
                             : ((c/255 + 0.055)/1.055)^2.4
   L = 0.2126*f(R) + 0.7152*f(G) + 0.0722*f(B)
   ```

3. **Threshold L > 0.179** → glyph = **black `#000000`**; else glyph =
   **white `#FFFFFF`**. (0.179 is the WCAG crossover that keeps contrast ≥ 4.5:1
   against both black and white for mid-tones.)
4. Apply by tinting: keep the icon's alpha, replace RGB with the chosen colour
   (multiply white master × tint, or `RecolorBrush`). The bottom text label uses
   the **same** computed colour, replacing the hard-coded `Color.White`.

A cheaper integer approximation (no gamma), acceptable at this size:
`Y = (R*299 + G*587 + B*114) / 1000; glyph = (Y > 140) ? black : white`.
Use the WCAG version for correctness; the fast version is fine in practice.

Optional: add a 1 px **black drop-shadow / 1 px contrasting outline** on the
glyph so it survives against busy or mid-luminance backgrounds regardless of
threshold edge cases.

---

## 4. State differentiation via icon (toggles)

Yes — change the **glyph**, not just the background. Background colour alone is a
weak signal at a glance; silhouette change is read pre-attentively.

- **Light ON/OFF:** `lightbulb` (filled) when ON, `lightbulb-outline` when OFF.
  Pair with bg: ON = warm yellow `#E0B432`, OFF = dim grey `#3A3A3A`. The
  filled-vs-outline contrast is the primary cue; colour reinforces.
- **Pi-hole:** `shield-check` (blocking, green bg) vs `shield-off` (paused,
  grey/amber bg).
- **Vacuum running vs docked:** `robot-vacuum` (running, active purple bg) vs
  `home-import-outline`/`robot-vacuum` + small dock glyph, or simply
  `robot-vacuum` (running) vs `power-plug` (docked/charging). Recommended:
  running = filled robot on bright bg; docked = same robot dimmed (apply the
  existing `IsDimmed` 30 % black overlay) + small charge pip.

Mechanism: extend the conditional-rule output so a rule can select an
`IconReference` (not only background/label). The model already supports
first-match-wins rules over the numeric value — add an icon field to the rule
result so `ON`/`OFF` map to `builtin:lightbulb` / `builtin:lightbulb-outline`.

---

## 5. Room/location differentiation for temperature

Three identical thermometers. **Recommendation: the live value already
differentiates them — make the number the dominant element**, and add a **single
uppercase initial badge** for identity.

Ranked options:
1. **(Recommended) Number-forward + corner initial.** Show the reading large
   ("21°") as the tile's main text, thermometer glyph small in a corner, and a
   single bold initial (`O` Office / `S` Salon / `P` Playroom) in the opposite
   corner. One glyph of room ID costs ~12×12 px — affordable. Rooms are
   distinguished by *value + letter*, which is unambiguous and localisation-free.
2. Fill-level thermometers (relative warmth): pretty but encodes temperature
   *twice* and still doesn't say which room — reject as the primary cue.
3. Secondary colour tint per room (e.g. bg hue shift O/S/P): works as a
   *reinforcement* layered on option 1, but colour-only ID fails for ~8 % of
   users (colour-blind) — never sole cue.
4. Different icon style per room: wastes the shared "this is temperature"
   affordance — reject.

Practical pixel budget at 72 px: value text 22–24 px centre, thermometer 16 px
top-left, room initial 14 px top-right. Fits.

---

## 6. Icon sources (freely licensed, small-size quality, .NET usable)

All are **SVG sets → pre-rasterise at build time** (§1). None are consumed as
SVG at runtime; ImageSharp loads the resulting PNGs natively. Quality notes are
for ~44–60 px rasterisation.

| Set | License | SVG availability | @44–60 px | Notes |
|---|---|---|---|---|
| **Material Design Icons (Pictogrammers MDI)** | **Apache 2.0** | Yes — 7000+ individual SVGs, named (`thermometer`, `water`, `cloud`, `lightbulb`, `lightbulb-outline`, `robot-vacuum`, `shield-check`, `molecule-co2`, `water-percent`). | Excellent — drawn on 24 px grid, clean at small sizes; covers **every** symbol this project needs. | **Top pick** — most complete domestic/IoT coverage; Apache 2.0 is permissive. |
| **Tabler Icons** | **MIT** | Yes — 5000+ SVGs, 24 px grid, consistent 2 px stroke. | Excellent; uniform stroke weight reads very cleanly when downscaled. | Outline-only (good for OFF states; pair with MDI filled for ON). |
| **Phosphor Icons** | **MIT** | Yes — 6 weights incl. `fill` and `regular`. | Excellent; the `fill` weight gives strong silhouettes ideal at 44 px. | Has `thermometer`, `drop`, `cloud`, `lightbulb`, `shield`, `robot`. Good filled/outline pairs for state. |
| **Lucide** (Feather fork) | **ISC** (Feather parts MIT) | Yes — outline, 24 px. | Very good; thin strokes — bump stroke to ≥2 px before rasterising. | Outline aesthetic; fewer device-specific glyphs (no robot-vacuum). |
| **Heroicons** | **MIT** | Yes — solid + outline @ 24/20 px. | Good. | Smaller catalogue; lacks IoT-specific icons (no thermometer/vacuum nuance). |
| **Font Awesome Free** | Icons **CC-BY 4.0**, fonts **SIL OFL 1.1**, code MIT | Yes (free tier subset). | Good. | CC-BY requires attribution; smaller free set. Prefer MDI/Tabler to avoid attribution burden. |

**Verdict:** standardise on **Material Design Icons (Apache 2.0)** as the single
source for the whole vocabulary (it has filled + `-outline` pairs for every
toggle state needed), with **Phosphor `fill`** as a fallback for any glyph whose
MDI silhouette is too thin. Both are .NET-friendly via the build-time PNG step;
no runtime SVG dependency, no attribution-in-UI requirement (keep a NOTICE file
for Apache 2.0 / MIT in the repo).

### Concrete icon → reference map (proposed `builtin:` names)

| Tile | `IconReference` | MDI source name |
|---|---|---|
| CO₂ | `builtin:co2` | `molecule-co2` (or `cloud`) |
| Temperature | `builtin:thermometer` | `thermometer` |
| Humidity | `builtin:humidity` | `water-percent` |
| Light ON | `builtin:light-on` | `lightbulb` |
| Light OFF | `builtin:light-off` | `lightbulb-outline` |
| Vacuum running | `builtin:vacuum-on` | `robot-vacuum` |
| Vacuum docked | `builtin:vacuum-dock` | `power-plug` (dimmed robot) |
| Pi-hole on | `builtin:dns-on` | `shield-check` |
| Pi-hole off | `builtin:dns-off` | `shield-off` |
| Nav left/right | keep generated `◄`/`►` | — (Unicode geometric, reliable) |
