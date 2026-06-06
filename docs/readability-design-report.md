# Stream Deck MK.2 — Readability & Information-Design Report

Target: Elgato Stream Deck MK.2. Each key = **72×72 px LCD**, ~20×20 mm physical, ~8 mm inter-key gap, viewed from ~50 cm. Scale: **1 px ≈ 0.28 mm**. Renderer: SixLabors.ImageSharp (C#).

All contrast ratios below use the WCAG formula (sRGB → linearised relative luminance, ratio = (L_hi+0.05)/(L_lo+0.05)). All "perceived luminance" values use the requested simple weighted formula `L = 0.2126·R + 0.7152·G + 0.0722·B` on the 0–1 sRGB channels (no gamma) — this is what the threshold rule should compute at runtime because it is cheap and monotonic enough for a binary text-colour decision.

---

## 1. Adaptive text colour

### Threshold
Use perceived luminance `L` (0–1, simple weighted, no gamma) of the **background**:

```
darkText = (L >= 0.45)   // use #1A1A1A
whiteText = (L <  0.45)  // use #FFFFFF
```

Why 0.45: a grey-ramp crossover (where charcoal #1A1A1A beats white #FFFFFF on WCAG ratio) lands at perceived L ≈ 0.49 for neutral greys, but chromatic backgrounds (especially blue, whose blue channel barely contributes to luminance but is dark) sit lower. **0.45** is the safe operating point — it picks the higher-contrast text colour for every real background below, and gives margin for future colours. Dark text colour = **#1A1A1A** (not pure black: softer, less haloing on the LCD, still effectively maximal contrast).

### Per-colour decision (computed)

| Background | Name | perceived L | white ratio | dark #1A1A1A ratio | **Pick** | best |
|---|---|---|---|---|---|---|
| #E74C3C | red alert | 0.423 | 3.82 | **4.56** | **DARK** | 4.56 |
| #E67E22 | orange warn | 0.555 | 2.85 | **6.11** | **DARK** | 6.11 |
| #27AE60 | green ok | 0.548 | 2.87 | **6.06** | **DARK** | 6.06 |
| #2980B9 | blue normal | 0.446 | **4.30** | 4.05 | **WHITE** | 4.30 |
| #FFD700 | yellow ON | 0.816 | 1.40 | **12.41** | **DARK** | 12.41 |
| #444444 | grey OFF | 0.267 | **9.74** | 1.79 | **WHITE** | 9.74 |
| #3498DB | blue vacuum ON | 0.532 | 3.15 | **5.52** | **DARK** | 5.52 |
| #555555 | dark placeholder | 0.333 | **7.46** | 2.33 | **WHITE** | 7.46 |

Note: red #E74C3C with white is **3.82:1** — passes large-text (≥3:1) but **fails** normal-text (4.5:1). Dark text on red gives 4.56:1 and passes both. The current "always white" rule is wrong for 5 of the 8 colours (orange, green, yellow, vacuum-blue, and arguably red).

### Should icons adapt too?
**Yes.** The icon glyph/symbol is foreground and must obey the same rule as the label: compute the background L once per tile, then render *both* the label and the icon symbol in the chosen ink colour (#1A1A1A or #FFFFFF). See §1-icon-redesign below — the icon should lose its own coloured square and become a monochrome glyph in the tile's ink colour, which automatically inherits the correct adaptive colour.

---

## 2. Information hierarchy — sensor tiles (temperature, CO₂, humidity)

### Problem
`"23.5 C"` renders value + unit at the same 15 px as a room name. No hierarchy; the *value* (primary) competes with everything.

### Legibility floor at 50 cm
1 px ≈ 0.28 mm. Approximate cap-height legibility threshold at 50 cm for a non-expert reader ≈ 5 mm for "instant glance," ~4.5 mm marginal.
- 24 px ≈ 6.7 mm — comfortably readable (primary value).
- 16 px ≈ 4.5 mm — marginal (acceptable only for secondary).
- 11–12 px ≈ 3.1–3.4 mm — sub-threshold for glance reading; only OK for tertiary context you don't have to read.

### Proposed sensor layout (72×72, origin top-left, y down)

```
+------------------------------------+
|  ⌂glyph(20px)            [staleness]|  icon zone: glyph 20px @ (6,6), ink colour
|                                     |
|            2 3 . 5                  |  VALUE: 30 px bold, centred, baseline ~ y=44
|               °C                    |  UNIT:  14 px,    centred, baseline ~ y=60
|            Bureau                   |  LABEL: 11 px,    centred, baseline ~ y=70
+------------------------------------+
```

Exact spec:
- **Value**: font 30 px (≈8.4 mm cap height) **bold**, horizontally centred at x=36, vertical centre ~y=38 (baseline ≈ y=44). This is the only thing readable across the desk. For 4-char values like `100.0` drop to 26 px; auto-shrink if measured width > 64 px.
- **Unit** (`°C`, `ppm`, `%`): 14 px regular, centred x=36, baseline ≈ y=60. Render in the ink colour at ~75% opacity to demote it.
- **Category icon**: monochrome glyph **20 px** at top-left (6,6), ink colour. It is a quiet identifier, not the hero — the big number is the hero.
- **Room/context label** (`Bureau`): 11 px, centred x=36, baseline ≈ y=70 — only if the icon doesn't already disambiguate (e.g. two temperature tiles for different rooms). If a tile is unique per category, drop the label and let the value go even bigger (34 px, vertically centred).

Recommendation: **strip the value+unit concatenation**. Store value and unit separately and render them as two text runs so each gets its own size.

---

## 3. Information hierarchy — toggle tiles (lights, vacuum, Pi-hole)

State is already encoded by background colour (the strongest, fastest channel). The label only needs to identify *which* device.

### Is the room-name label necessary?
For a fixed physical layout the user memorises positions within days; the **icon alone** is usually enough. But text is the safest identifier for guests / infrequent buttons. Compromise:
- Keep a **single icon as the primary identifier**, large.
- Keep a **short label** as backup, small.

### Proposed toggle layout (72×72)

```
+------------------------------------+
|                                     |
|             [ICON 40px]             |  glyph centred, x=36, vertical centre ~y=30
|                                     |
|             Bureau                  |  LABEL 13 px, centred, baseline ~y=64
+------------------------------------+
```

- **Icon**: 40 px monochrome glyph, centred horizontally, optical centre ~y=28–30, ink colour (adaptive).
- **Label**: 13 px (≈3.6 mm), centred x=36, baseline ≈ y=64, ink colour.

### Should ON/OFF differ typographically?
State is *already* signalled instantly by background colour and by the adaptive ink flip — that is faster than any size change. **Do not** change text size between states (it makes tiles "jump" and looks like a glitch). Keep label size/position identical ON vs OFF; only the background colour and the (adaptive) ink colour change. Optionally render the OFF icon glyph at ~85% opacity to read as "dormant," but keep geometry stable.

---

## 4. Contrast ratios — white text on each background (computed)

| Background | white ratio | Passes white? | Fix |
|---|---|---|---|
| #E74C3C red | 3.82 | large-only (fails 4.5) | **#1A1A1A → 4.56** ✓ |
| #E67E22 orange | 2.85 | **FAIL** | **#1A1A1A → 6.11** ✓ |
| #27AE60 green | 2.87 | **FAIL** | **#1A1A1A → 6.06** ✓ |
| #2980B9 blue | 4.30 | normal-text ✓ (≥4.5 marginal miss) | keep **white 4.30**; or bump bg to #1F6699 for ≥4.5 |
| #FFD700 yellow | 1.40 | **FAIL badly** | **#1A1A1A → 12.41** ✓ |
| #444444 grey | 9.74 | ✓ | keep white |
| #3498DB vacuum | 3.15 | large-only | **#1A1A1A → 5.52** ✓ |
| #555555 placeholder | 7.46 | ✓ | keep white |

Key finding: **orange, green, and yellow fail outright with white**; red and vacuum-blue fail normal-text. All are fixed by the §1 adaptive rule. Blue #2980B9 is the one colour where white wins (4.30) but is just shy of the 4.5 normal-text bar — since the big value is "large text" (≥24 px bold qualifies as large under WCAG, threshold 3:1) it passes comfortably; for the small 11 px label consider darkening the blue to **#1F6699** (white ratio ≈ 5.3) if you want AA on the label too.

---

## 5. Font-size recommendations

Current 11 pt ≈ 15 px ≈ 4.2 mm cap → **borderline/sub-threshold** at 50 cm for primary data. Fine for a tertiary label, too small for a value.

| Role | Tile type | Size (px) | ≈ cap height | Weight |
|---|---|---|---|---|
| Numeric VALUE (primary) | sensor | **30 px** (26 if ≥5 chars) | 8.4 mm | Bold |
| UNIT (secondary) | sensor | **14 px** | 3.9 mm | Regular, 75% opacity |
| Room/category LABEL (tertiary) | sensor | **11 px** | 3.1 mm | Regular |
| Device LABEL | toggle | **13 px** | 3.6 mm | Regular/Medium |
| ICON glyph | sensor | 20 px | — | — |
| ICON glyph | toggle | 40 px | — | — |

**Yes — sensor and toggle tiles should use different typographic layouts.** Sensor tiles are *data displays* (value-dominant, number is hero). Toggle tiles are *controls* (icon-dominant, state via colour). Treating them identically is the root of the "three temperature tiles look identical" problem — give sensors a big centred number and they instantly differentiate by value.

---

## 6. Dim / stale overlay

### Problem with 30% black
A 30% opaque black overlay multiplies less, in absolute luminance terms, on already-dark tiles. On #444444 (grey OFF, white text) it pushes the background toward black and *raises* white-text contrast slightly — harmless. But on a stale **yellow** sensor tile with the new dark ink, darkening the yellow lowers its luminance and reduces dark-on-yellow contrast; and on dark tiles a black veil can make white text "muddy" near the edges. The overlay's effect is non-uniform across the palette — exactly the inconsistency to avoid for a "stale" signal that must read the same everywhere.

### Recommendation — desaturate, don't darken
Apply a **luminance-preserving desaturation** toward grey (move each pixel a fixed fraction toward its own luminance) rather than a black veil:

```
grey = 0.2126*R + 0.7152*G + 0.0722*B   // per pixel, sRGB
R' = lerp(R, grey, 0.6)  // 60% desaturation
G' = lerp(G, grey, 0.6)
B' = lerp(B, grey, 0.6)
```

This keeps brightness (so text contrast is preserved everywhere) while killing the chroma that signals "live state" — a stale tile reads as "drained of colour," which is semantically perfect and palette-independent. Optionally add a tiny uniform −8% lightness *after* desaturation for extra cue. Re-run the adaptive-ink decision on the **desaturated** background L so the text colour stays correct. Add a small clock/stale glyph in the top-right corner as an explicit marker.

---

## 7. Spacing & padding

The physical ~8 mm bezel between keys already provides strong visual separation, so the renderer does **not** need internal margin for separation — but it does need it so content doesn't kiss the glass edge (which the eye reads as "clipped"). 6 px (1.7 mm) is a bit tight for text bottoms.

Recommended safe zones (72×72):
- **Outer safe margin: 6 px** all sides for backgrounds-fills is fine (fill the whole 72×72 with the background colour — edge-to-edge colour is good, the bezel frames it).
- **Text safe area: 8 px** left/right, so labels live within x∈[8,64] (max text width 56–64 px before auto-shrink).
- **Bottom padding: 8 px** — keep label baselines at ≤ y=64 so descenders (e.g. "g" in a name) don't clip at y=72. Current y=57 baseline + 15 px font is OK but tighten the new 11–13 px labels to baseline y=64–70 max.
- **Top icon padding: 6 px** at (6,6).
- **Inter-zone gap (value→unit→label):** ≥ 2 px optical gap; with the sizes above the baselines (44 / 60 / 70) already give clean separation.

Fill the background edge-to-edge (the bezel is the border); pad only the *content* (text + glyph) by the safe areas above.

---

## Implementation summary (for ImageSharp)

1. Compute `L = 0.2126*r + 0.7152*g + 0.0722*b` (0–1) of the resolved background once per render.
2. `ink = L >= 0.45 ? #1A1A1A : #FFFFFF`. Use `ink` for **both** label and icon glyph.
3. Drop the icon's coloured square; render icons as monochrome glyphs in `ink`.
4. Sensor tiles: value 30 px bold (auto-shrink ≥5 chars), unit 14 px @75% opacity, 20 px corner glyph, optional 11 px label. Store value/unit separately.
5. Toggle tiles: 40 px centred glyph + 13 px label; identical geometry ON vs OFF, colour does the talking.
6. Stale = 60% desaturation toward per-pixel luminance + corner clock glyph; recompute ink on desaturated L.
7. Fill background 72×72 edge-to-edge; constrain text to x∈[8,64], baselines ≤ y=70, icon at (6,6).
