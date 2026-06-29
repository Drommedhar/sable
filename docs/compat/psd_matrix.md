# PSD / PSB Compatibility Matrix

**Project:** Sable
**Source:** `src/Sable.Format/PsdReader.cs` (+ `src/Sable.Engine/Layers/*`, `src/Sable.Imaging/TextRaster.cs`)
**Audited:** 2026-06-24
**Status labels:** `SUPPORTED` · `PARTIAL` · `IMPORTED_AS_RASTER` · `UNSUPPORTED` · `UNKNOWN`

This matrix records, for every PSD construct Sable's importer encounters:

1. whether it imports **visually**
2. whether it imports **structurally** (editable in Sable's layer graph)
3. whether it is **preserved on save** (`.sable` round-trip; PSD *export* is not yet implemented)
4. whether a **test fixture** exists
5. what the user sees when a feature is **unsupported** (warning text)

It is the single source of truth referenced by the import compatibility report UI
(`CompatibilityReportWindow`) and by `tests/Sable.Tests/PsdReaderTests.cs`.

---

## 1. File container

| Construct | Visual | Structural | Save | Fixture | Status |
|---|---|---|---|---|---|
| PSD v1 (8BPS, version 1) | yes | yes | `.sable` | yes | **SUPPORTED** |
| PSB (version 2, large document) | — | — | — | yes | **UNSUPPORTED** — rejected with `"PSB (large document format) is not supported — re-save as PSD."` |
| Corrupt / non-PSD | — | — | — | yes | **UNSUPPORTED** — rejected with `"Not a PSD file (missing 8BPS signature)."` |

## 2. Colour mode & bit depth

| Mode | Depth | Visual | Structural | Save | Fixture | Status |
|---|---|---|---|---|---|---|
| RGB | 8-bit | yes | yes | yes | yes | **SUPPORTED** |
| RGB | 16-bit | yes | yes (→8-bit) | yes | yes | **PARTIAL** — converted to 8-bit; warning `"16-bit document converted to 8-bit."` |
| Grayscale | 8-bit | yes | yes (→RGB gray) | yes | yes | **SUPPORTED** |
| Grayscale | 16-bit | yes | yes (→8-bit) | yes | yes | **PARTIAL** — high-byte truncation, same warning |
| RGB | 32-bit | — | — | — | yes | **UNSUPPORTED** — `"32-bit PSD layers are not supported"` |
| CMYK / Lab / Indexed / Bitmap / Multichannel / Duotone | any | — | — | — | — | **UNSUPPORTED** — `"Unsupported colour mode {name} — only RGB and Grayscale PSDs import."` |

## 3. Compression

| Method | Status | Fixture |
|---|---|---|
| Raw (0) | **SUPPORTED** | yes |
| RLE / PackBits (1) | **SUPPORTED** | yes |
| ZIP without prediction (2) | **SUPPORTED** | yes |
| ZIP with prediction (3) | **SUPPORTED** | yes |

## 4. Layer basics

| Construct | Visual | Structural | Save | Fixture | Status |
|---|---|---|---|---|---|
| Raster layer (pixel content) | yes | yes (`PixelLayer`, own W/H/offset) | yes | yes | **SUPPORTED** |
| Layer opacity | yes | yes | yes | yes | **SUPPORTED** |
| Fill opacity (`iOpa`) | yes | yes (`FillOpacity`) | yes | yes | **SUPPORTED** |
| Visibility flag | yes | yes | yes | yes | **SUPPORTED** |
| Layer name (Pascal + `luni` unicode) | yes | yes | yes | yes | **SUPPORTED** |
| Per-layer bounds / offset | yes | yes (`OffsetX/Y`) | yes | yes | **SUPPORTED** |
| Empty (zero-rect) raster layer | yes | yes (1×1) | yes | yes | **SUPPORTED** |
| Flattened file (no layer info) | yes | yes (single `PixelLayer` from composite) | yes | yes | **SUPPORTED** |

## 5. Blend modes

| PS key | Sable `BlendMode` | Status |
|---|---|---|
| norm / pass | Normal | **SUPPORTED** |
| mul, scrn, over, sLit, hLit, vLit, lLit, pLit, hMix | Multiply/Screen/Overlay/SoftLight/HardLight/VividLight/LinearLight/PinLight/HardMix | **SUPPORTED** |
| dark, lite, dkCl, lgCl | Darken/Lighten/DarkerColor/LighterColor | **SUPPORTED** |
| idiv, div, lbrn, lddg | ColorBurn/ColorDodge/LinearBurn/Add | **SUPPORTED** |
| diff, smud, fsub, fdiv | Difference/Exclusion/Subtract/Divide | **SUPPORTED** |
| hue, sat, colr, lum | Hue/Saturation/Color/Luminosity | **SUPPORTED** |
| diss (Dissolve) | → Normal | **PARTIAL** — warning `"Dissolve blend mapped to Normal."` |
| unknown key | → Normal | **PARTIAL** — warning `"unknown blend mode '{key}' mapped to Normal."` |

## 6. Groups

| Construct | Visual | Structural | Save | Fixture | Status |
|---|---|---|---|---|---|
| Open / closed folder (`lsct` 1/2) | yes | yes (`GroupLayer`) | yes | yes | **SUPPORTED** |
| Pass-through group (`pass` blend) | yes | yes (`PassThrough=true`) | yes | yes | **SUPPORTED** |
| Nested groups | yes | yes (recursive `Children`) | yes | yes | **SUPPORTED** |
| Unbalanced group markers (corrupt) | yes | flattened | yes | yes | **PARTIAL** — warning `"Unbalanced group markers — group structure flattened."` |

## 7. Masks

| Construct | Visual | Structural | Save | Fixture | Status |
|---|---|---|---|---|---|
| Layer raster mask (ch −2) | yes | yes (layer-aligned `Mask`) | yes | yes | **SUPPORTED** |
| Mask default colour | yes | yes (fill before blit) | yes | yes | **SUPPORTED** |
| Disabled mask | — | dropped | — | yes | **PARTIAL** — warning `"disabled layer mask dropped."` |
| Vector mask (`vmsk`/`vsms` bezier) | yes | rasterised into mask coverage | yes (as mask) | yes | **IMPORTED_AS_RASTER** — warning `"vector mask rasterised"`; not editable as a path post-import |
| Real (vector) mask composite (ch −3) | — | ignored | — | yes | **UNSUPPORTED** — silently skipped (vector mask path is the source of truth) |

## 8. Clipping masks

| Construct | Visual | Structural | Save | Fixture | Status |
|---|---|---|---|---|---|
| Clip-to-below flag | yes | yes (`ClipToBelow`) | yes | yes | **SUPPORTED** |
| Clipping chain (multiple clipped layers) | yes | yes (each layer `ClipToBelow`) | yes | yes | **SUPPORTED** |
| Clipped group | yes | yes | yes | yes | **SUPPORTED** |

## 9. Text layers (TySh)

| Construct | Visual | Structural | Save | Fixture | Status |
|---|---|---|---|---|---|
| Point text | yes | yes (`TextLayer`, editable) | yes | yes | **SUPPORTED** |
| Area / paragraph text (`BoxBounds`) | yes | yes (`BoxWidth`) | yes | yes | **SUPPORTED** |
| Font family (PS name → installed) | yes | yes (`MapPsFont`) | yes | yes | **PARTIAL** — best-effort match; missing fonts flagged via toast |
| Size, tracking, leading | yes | yes | yes | yes | **SUPPORTED** |
| Faux bold / italic | yes | yes | yes | yes | **SUPPORTED** |
| Underline / strikethrough | yes | yes | yes | yes | **SUPPORTED** |
| Fill colour (first run) | yes | yes | yes | yes | **SUPPORTED** |
| Justification (L/R/C) | yes | yes | yes | yes | **SUPPORTED** |
| Baked rotation / scale (matrix) | yes | yes (`Rotation`, size×scale) | yes | yes | **PARTIAL** — anchor drift vs PS for large angles accepted |
| Multi-style runs (mixed formatting in one layer) | first run only | first run only | first run only | yes | **PARTIAL** — Sable `TextLayer` is single-style; later runs flattened to the first; warning `"text layer has N style runs — flattened to first style."` |
| Text warp | — | — | — | yes | **UNSUPPORTED** — warning `"text warp not imported (flattened to un-warped text)."` |
| Vertical text | — | — | — | yes | **UNSUPPORTED** — warning `"vertical text not imported (flattened to horizontal)."` |
| Baseline shift / super-subscript | — | — | — | yes | **UNSUPPORTED** — warning `"baseline shift not imported."` / `"superscript not imported."` / `"subscript not imported."` |
| All caps / small caps | — | — | — | yes | **UNSUPPORTED** — warning `"all-caps not imported."` / `"small-caps not imported."` |
| OpenType features | — | — | — | yes | **UNSUPPORTED** — warning `"OpenType features not imported."` |
| Text on path | — | — | — | — | **UNSUPPORTED** — not mapped from PSD (Sable has its own text-on-path, no PSD bridge) |
| Unreadable TySh | rasterised | rasterised | yes | — | **IMPORTED_AS_RASTER** — warning `"text layer rasterised (style data unreadable)"` |

## 10. Vector / shape layers

| Construct | Visual | Structural | Save | Fixture | Status |
|---|---|---|---|---|---|
| Solid-colour fill layer + single closed vector mask (`SoCo`+`vmsk`) | yes | yes (`PathLayer`, editable bezier) | yes | yes | **SUPPORTED** — bridged to an editable `PathLayer` (fill colour × preserved bezier knots) when the mask is a single closed contour |
| Solid-colour fill layer + multi closed vector mask (`SoCo`+`vmsk`) | yes | yes (`PathLayer` + `ExtraContours`, editable) | yes | yes | **SUPPORTED** — bridged to an editable `PathLayer` whose primary sub-path is the first contour and the rest become `ExtraContours` (even-odd fill → holes); warning `"solid-colour fill layer imported as editable shape (multi-contour)"` |
| Solid-colour fill layer + open vector mask (`SoCo`+`vmsk`) | yes | rasterised `PixelLayer` shaped to path bbox | yes | yes | **IMPORTED_AS_RASTER** — fallback when any contour is open; warning `"fill layer rasterised"` |
| Solid-colour fill layer, no mask (`SoCo`) | yes | canvas-wide `PixelLayer` | yes | — | **IMPORTED_AS_RASTER** |
| Gradient fill (`GdFl`) | — | — | — | — | **UNSUPPORTED** — skipped with warning |
| Pattern fill (`PtFl`) | — | — | — | — | **UNSUPPORTED** — skipped with warning |
| Shape layer (vmsk on a content layer) | yes | rasterised into mask | yes | — | **IMPORTED_AS_RASTER** |

## 11. Adjustment layers

| PS key | Name | Status |
|---|---|---|
| brit | Brightness/Contrast | **SUPPORTED** — mapped to editable `AdjustmentLayer` (BrightnessContrast) |
| levl | Levels | **SUPPORTED** — mapped (composite-channel InBlack/White/Gamma) |
| curv | Curves | **SUPPORTED** — mapped (per-channel bezier points → `Curves`) |
| expA | Exposure | **SUPPORTED** — mapped (stops) |
| vibA | Vibrance | **SUPPORTED** — mapped |
| hue2 / hue | Hue/Saturation | **SUPPORTED** — mapped (HueShift/Saturation/Lightness) |
| blnc | Color Balance | **SUPPORTED** — mapped (shadow/mid/high RGB shifts) |
| blwh | Black & White | **SUPPORTED** — mapped (R/G/B luminance weights) |
| mixr | Channel Mixer | **SUPPORTED** — mapped (3×3 matrix) |
| nvrt | Invert | **SUPPORTED** — mapped (no params) |
| post | Posterize | **SUPPORTED** — mapped (levels) |
| thrs | Threshold | **SUPPORTED** — mapped (luminance cut) |
| grdm | Gradient Map | **SUPPORTED** — mapped (gradient stops → `GradientStops`) |
| phfl | Photo Filter | **SUPPORTED** — mapped approximately to White Balance (filter colour hue → temperature/tint, density → strength) |
| selc | Selective Color | **UNSUPPORTED** — skipped with warning |
| clrL | Channel Mixer (legacy variant) | **SUPPORTED** — mapped to Channel Mixer (same as `mixr`) |

Mapped adjustments remain editable (sliders in `AdjustmentPanel`); round-trip in `.sable`.

## 12. Layer effects (lfx2)

| Effect | Visual | Structural | Save | Fixture | Status |
|---|---|---|---|---|---|
| Drop shadow (`DrSh`) | yes | yes (`LayerEffect.DropShadow`) | yes | yes | **SUPPORTED** |
| Inner shadow (`IrSh`) | yes | yes | yes | yes | **SUPPORTED** |
| Outer glow (`OrGl`) | yes | yes | yes | yes | **SUPPORTED** |
| Inner glow (`IrGl`) | yes | yes | yes | yes | **SUPPORTED** |
| Colour overlay (`SoFi`) | yes | yes | yes | yes | **SUPPORTED** |
| Stroke (`FrFX`) | yes | yes (size + position) | yes | yes | **SUPPORTED** |
| Gradient overlay (`GrFl`) | yes | yes (2-stop ramp) | yes | yes | **PARTIAL** — only first/last stop; multi-stop gradients flatten to 2 colours; warning `"gradient overlay has N stops — flattened to first/last."` |
| Bevel / emboss (`ebbl`) | yes | yes | yes | yes | **PARTIAL** — contour curves / texture dropped; warnings `"bevel/emboss contour curve not imported."` / `"bevel/emboss texture not imported."` |
| Multiple instances per kind (`*Multi`) | yes | yes | yes | yes | **SUPPORTED** |
| Legacy effects (`lrFX` without `lfx2`) | — | — | — | yes | **UNSUPPORTED** — warning `"legacy layer effects not imported"` |
| Contour / noise / texture / pattern overlay / satin | — | — | — | — | **UNSUPPORTED** — dropped silently |
| Unreadable lfx2 | — | — | — | — | **PARTIAL** — warning `"layer effects unreadable"` |

## 13. Smart Objects & placed content

| Construct | Status |
|---|---|
| Smart Object (`SoLd`/`PlLd`/`SoLE`) | **IMPORTED_AS_RASTER** — warning `"smart object rasterised"`; embedded source data not preserved; no relink/edit. (Roadmap §14 Tier 1.) |

## 14. Artboards, slices, annotations

| Construct | Status |
|---|---|
| Artboards | **UNSUPPORTED** — not parsed; layers import, artboard framing lost |
| Slices | **UNSUPPORTED** — not parsed |
| Annotations / notes | **UNSUPPORTED** — not parsed |

## 15. Image resources / metadata

| Resource | Status |
|---|---|
| ICC profile (image resources) | **UNSUPPORTED** — colour mode data + image resources section skipped entirely; no profile preservation (roadmap Workstream 5) |
| EXIF / IPTC / XMP | **UNSUPPORTED** — skipped |
| Resolution (DPI) | **UNSUPPORTED** — not read from PSD (Sable has DPI on raster export via `ImageMeta`, not on PSD import) |

---

# Per-feature audit blocks (roadmap §19 format)

## Feature: PSD clipping masks

### Current status
**SUPPORTED**

### Current implementation
- importer path: `PsdReader.ParseLayerInfo` reads the clipping flag byte; `BuildPixelLayer`/`BuildTextLayer`/`BuildSoCoLayer`/`BuildAdjustmentLayer` set `ClipToBelow = rec.Clipping`.
- layer model path: `Layer.ClipToBelow` → `composite.wgsl` `params.clip` (mode 0 off / 1 backdrop-alpha / 2 base-alpha).
- renderer path: `GpuCompositor.BlendLayerSequence` resolves clip runs and stamps the base layer's standalone alpha into `_clipBase`; `BlendOneLayer` → `BlendInto`/`BlendDocContentWithFx` consume `CurrentClip`.

### Fixed (2026-06-29)
- **Bug:** clipped layers multiplied coverage by the *running backdrop alpha* (the whole accumulator below), so any opaque layer beneath the base (e.g. a background) drove backdrop alpha ≈ 1 across the canvas and the clip leaked everywhere — clipped content showed well outside the base shape. Photoshop clips to the **base layer's transparency only**, independent of what sits below it.
- **Fix:** clip mode 2 binds `_clipBase` (binding 8) = the base layer's standalone coverage (own content + mask + transform, rendered over a permanently-zero backdrop, FX/opacity excluded — matching PS, which clips to raw layer transparency). `BlendLayerSequence` finds the base (nearest non-clipped layer below the run), stamps it once per run, and every clipped layer in the chain clips to it. Nested effect children keep mode 1 (backdrop = parent content, correct there). Verified by the `clip-mask` GPU smoke (Sable.Gpu.Spike): background no longer leaks; clip shows only over the base.

### Known remaining gaps
- **Clipped adjustment layers** still use backdrop-alpha clip (`adjust.wgsl` has no `clipBase` binding) — a clipped Curves/Levels over an opaque background still leaks. Follow-up: thread `_clipBase` into the adjustment pass.

### Required tests
- GPU smoke: `clip-mask` (Sable.Gpu.Spike) — base/clip/background, asserts no leak. DONE.
- fixture: `psd/clipping_chain.psd` (3 clipped layers) — TODO
- fixture: `psd/clipping_with_mask.psd` — TODO

### Acceptance criteria
- clipped rendering matches reference within tolerance; no silent flattening; clip does not extend beyond the base layer's transparency. MET for pixel/shape/text/path/group bases.

---

## Feature: PSD groups (incl. pass-through)

### Current status
**SUPPORTED**

### Current implementation
- importer: `BuildTree` walks `lsct` section types (3 = divider bottom, 1/2 = folder top) with a stack; `pass` blend key → `GroupLayer.PassThrough`.
- layer model: `GroupLayer.Children`; compositor recurses.
- save: `.sable` serializes `Children` recursively.

### User-visible problems
- Pass-through groups blend correctly; isolated groups are Sable's default (matches PS default).

### Required tests
- fixture: `psd/nested_groups.psd`

---

## Feature: PSD text layers

### Current status
**PARTIAL**

### Current implementation
- importer: `ParseTySh` + `ApplyEngineData` (first style run only) → `BuildTextLayer` → editable `TextLayer`.
- font mapping: `MapPsFont` (alphanumeric-normalised prefix match against installed families, camel-case heuristic fallback).
- missing fonts: `ExtractFonts` → `MainWindow.OpenPsdTab` → toast.

### User-visible problems
- Multi-style runs flatten to the first run's style.
- Text warp, text-on-path, OpenType features, vertical text not imported.
- Anchor drift for large rotation angles.

### Proposed implementation
1. Keep single-style mapping (Sable's `TextLayer` is single-style by design).
2. Add a warning when a TySh has >1 style run: `"text layer has multiple styles — flattened to first style."`
3. Surface warp / text-on-path / OpenType as explicit unsupported warnings instead of silent skip.

### Required tests
- fixture: `psd/text_point.psd` (exists as synthetic)
- fixture: `psd/text_multistyle.psd` (new — asserts warning)
- fixture: `psd/text_missing_font.psd`

---

## Feature: PSD vector / shape layers

### Current status
**SUPPORTED** (single closed contour) / **SUPPORTED** (multi closed contour → `PathLayer` + `ExtraContours`) / **IMPORTED_AS_RASTER** (open contour fallback)

### Current implementation
- importer: `ParseVectorMaskKnots` preserves the bezier knots (anchor + in/out handles, doc px); `BuildSoCoLayer` bridges a single closed contour to an editable `PathLayer` (fill colour × `PathNode` list with handles), bridges multi-closed-contour to a `PathLayer` whose primary sub-path is the first contour and the rest become `ExtraContours` (even-odd fill → holes), and falls back to `RasterizeCoverage` → `PixelLayer` for open contours.
- `PathNode` gained a 6-arg constructor (anchor + handles) so PSD bezier handles survive.
- Sable `ShapeLayer` (parametric kinds) is not used — arbitrary bezier paths go to `PathLayer`.

### User-visible problems
- Open vector masks still rasterise (not editable as paths).
- Stroke preservation on vector masks not yet (coverage/fill only).

### Required tests
- fixture: `psd/shape_rect.psd` (exists — single closed → PathLayer)
- fixture: `psd/shape_multi.psd` (exists — multi-contour → rasterised fallback)

### Acceptance criteria
- single closed contour → editable `PathLayer` (Pen/Node tools work); round-trip in `.sable`.

---

## Feature: PSD adjustment layers

### Current status
**SUPPORTED** (15 of 16 kinds mapped; 1 skipped)

### Current implementation
- importer: `MappableAdjustmentKeys` set → `ParseLayerInfo` parses the descriptor body into `rec.AdjustmentDesc`; `BuildAdjustmentLayer` maps each key to an `AdjustmentKind` + params via per-kind mappers (`BuildBrightnessContrast`/`BuildLevels`/`BuildCurves`/…/`BuildGradientMap`/`BuildPhotoFilter`).
- layer model: `Sable.Engine/Layers/AdjustmentLayer` (existing); params feed `PackParams` → `adjust.wgsl`.
- save: `.sable` serializes `AdjustmentLayer` (existing).

### User-visible problems
- Selective Color (`selc`) still skipped with warning.
- Levels maps the composite channel only (per-channel levels not yet).
- Curves maps per-channel points; PS contour/curve presets dropped.
- Photo Filter maps approximately to White Balance (no direct photo-filter kind; preserve-luminosity ignored).

### Required tests
- fixture: `psd/adj_brightness.psd` (exists)
- fixture: `psd/adj_curves.psd` (exists)
- fixture: `psd/adj_invert.psd` (exists)
- fixture: `psd/adj_skipped.psd` (Photo Filter — exists, asserts skip warning)

### Acceptance criteria
- mapped adjustments remain editable (sliders in `AdjustmentPanel`); round-trip in `.sable`.

---

## Feature: PSD layer effects (lfx2)

### Current status
**PARTIAL** (SUPPORTED for the common set)

### Current implementation
- importer: `ParseLfx2` → `List<LayerEffect>`; `MapFxBlend`; CC multi-instance lists.
- renderer: `GpuCompositor.BlendContentWithFx` + `fx.wgsl`.

### User-visible problems
- Gradient overlay flattens to 2 colours.
- Bevel contour/texture dropped.
- Legacy `lrFX` not imported.

### Proposed implementation
- Keep as is for Phase 1; add explicit warnings for dropped sub-params (contour, noise, texture).

### Required tests
- fixture: `psd/fx_shadow.psd` (exists as synthetic)
- fixture: `psd/fx_gradient_overlay.psd` (asserts 2-colour flatten warning)

---

## Feature: Smart Objects

### Current status
**IMPORTED_AS_RASTER**

### Current implementation
- importer: `SoLd`/`PlLd`/`SoLE` → `Notes.Add("smart object rasterised")`; embedded source discarded.

### User-visible problems
- No edit/relink; embedded data lost.

### Proposed implementation (roadmap §14)
- Tier 1 (now): keep raster fallback, preserve original source bytes in a sidecar `.sable` resource so future Tier 2 can re-open them.
- Tier 2/3: deferred.

### Required tests
- fixture: `psd/smart_object.psd` (asserts rasterisation warning)

---

## Feature: Import compatibility report UI

### Current status
**PARTIAL** — transient toasts only.

### Current implementation
- `MainWindow.OpenPsdTab` → `ShowToast` (first 12 warnings + missing fonts).

### User-visible problems
- Warnings vanish when the toast is dismissed; no way to review them later.
- No structured categorisation (unsupported layer types vs. rasterised vs. missing fonts vs. unsupported modes).

### Proposed implementation
1. `CompatibilityReportWindow` — a persistent, modeless window listing every warning categorised by severity/kind, plus the missing-font list.
2. Toast gains a "View report" action button that opens the window.
3. Window▸Compatibility Report re-opens the last report for the active tab.
4. Report is stored per-tab so it survives until the tab closes.

### Required tests
- headless: `PsdReader.Load` warnings categorisation (pure logic).
