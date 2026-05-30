# Sable — Cross-Platform AI Image Editor

**Plan v0.1 — 2026-05-29**

A cross-platform (Windows / Linux / macOS) raster image editor with a Photoshop/Affinity look-and-feel, GPU-first rendering, and a pluggable local-AI subsystem (smart selection, inpaint/repair, upscale, background removal) where the user picks the model (SD, Flux.1, Flux.2, Z-Image, Qwen-Image, and whatever comes next).

---

## 1\. Decisions locked from kickoff

| Question | Decision | Consequence |
| --- | --- | --- |
| Editing model | **Raster only** (v1) | One pixel-pipeline engine. Vector deferred to v2. Shapes/text exist but rasterize. |
| AI scope | Smart selection + inpaint + upscale/denoise + bg-removal, **all local, user-selectable model** | Needs a model-agnostic backend + local inference sidecar, not hardcoded ONNX. |
| Team | Small team (2–5) | Split into 3 tracks: **Engine**, **UI/UX**, **AI**. Each track owns a milestone lane. |
| GPU | **GPU-first** | Custom GPU compute pipeline from day one. Canvas is NOT Avalonia-drawn. |
| GPU API | **wgpu-native** | Committed. One API → DX12/Vulkan/Metal. |
| AI sidecar | **Diffusers only, opt-in install** | Settings "Install generative AI" button (Affinity-style). App provisions Python env on demand. **No ComfyUI.** |
| AI models | **User-provided only** | No bundled catalog. User imports own weights. License-safe. |
| AI compute | **GPU-only, no CPU fallback** | AI ops hard-gated by VRAM fit. No model runs on CPU. |
| License | **MIT, open source** | Diffusers (Apache-2.0) backend = no GPL. See §12 — ImageSharp/libraw caveats only. |
| PSD/Affinity import | PSD best-effort; **Affinity = research only** | Affinity `.afphoto` is closed, no spec — likely infeasible. |
| Tablet pressure | **Post-v1** | Mouse/pen-without-pressure in v1. |

---

## 2\. Tech stack

### 2.1 UI framework — Avalonia: confirmed, with a hard boundary

AvaloniaUI is the right call for the **chrome** (menus, dockable panels, tool palettes, dialogs, layer tree, property inspectors). Reasons:

*   True cross-platform .NET desktop (Win/Linux/macOS, single codebase), unlike WPF (Windows-only) or MAUI (mobile-leaning, weaker desktop docking).
*   Mature docking/panel ecosystem (`Dock.Avalonia`) — essential for a Photoshop-style multi-panel workspace.
*   Styling system (Fluent + custom themes) can reproduce the dark Affinity/PS aesthetic.
*   .NET keeps us in the same runtime as ONNX Runtime, ImageSharp, SkiaSharp, and Silk.NET — no FFI tax for most of the stack.

**Alternatives considered and why not:**

| Option | Verdict |
| --- | --- |
| Qt (C++/PySide) | Best-in-class desktop, but C++ team cost + worse .NET/AI-lib integration. |
| Electron | Canvas perf ceiling too low for GPU-first pro editor; RAM heavy. |
| Flutter Desktop | Weak desktop docking/menu maturity; Dart isolates the AI/.NET ecosystem. |
| Rust (Slint/egui) | Great perf, but smaller team velocity and thinner image/AI lib ecosystem. |

**The hard boundary:** Avalonia renders _everything except the canvas_. The image canvas is a **native GPU surface** (swapchain) embedded into the Avalonia visual tree via a native control host (`NativeControlHost` / platform child window or a shared GPU texture composited through Avalonia's compositor). Avalonia's own rendering (Skia) is fine for UI but not for a multi-layer GPU compositing engine at 60fps with 100+ megapixel documents.

### 2.2 GPU layer — cross-platform compute

GPU-first + cross-platform is the central engineering risk. Options:

| Approach | Win | Linux | macOS | .NET binding | Notes |
| --- | --- | --- | --- | --- | --- |
| **Vulkan via Silk.NET** | ✅ | ✅ | ⚠️ (MoltenVK) | ✅ native | Lowest level, max control, most work. |
| **wgpu-native** (Rust) via P/Invoke | ✅ DX12/Vk | ✅ Vk | ✅ Metal | binding to write | One API → all backends. Best portability/effort ratio. |
| ComputeSharp | ✅ DX12 | ❌ | ❌ | ✅ | Windows-only. Disqualified. |
| Veldrid | ✅ | ✅ | ✅ | ✅ | Lower activity, graphics-oriented (less compute-first). |

**Recommendation: wgpu-native** behind a thin .NET binding. One shader language (WGSL), one API, maps to DX12 / Vulkan / Metal natively. Avoids per-platform GPU branching. Silk.NET/Vulkan is the fallback if wgpu binding cost proves too high.

> **Decision needed (see §11):** wgpu-native vs Silk.NET/Vulkan. This is the single biggest fork in the engine track.

### 2.3 Supporting libraries

*   **Pixel/codec I/O**: `ImageSharp` (managed, cross-platform) for decode/encode of PNG/JPEG/TIFF/WebP; `SkiaSharp` for some raster ops + UI. Native libs for HEIC/RAW (libraw) via P/Invoke.
*   **Color management**: Little-CMS (`lcms2`) via binding — ICC profiles, sRGB/Display-P3/Adobe-RGB, 16-bit & float pipelines.
*   **AI runtime**: ONNX Runtime (.NET) for _light_ models (selection, matting, upscale, LaMa repair) — default, no Python. Generative diffusion (Flux/SD/Qwen) → **opt-in Diffusers sidecar** (see §6).
*   **Tiling/large docs**: custom tiled storage (see §4).

---

## 3\. Rendering & compositing architecture (GPU-first)

```
                ┌─────────────────────────────────────────┐
   Avalonia ───▶│  App chrome: panels, tools, menus, dialogs│
   (Skia UI)    └─────────────────────────────────────────┘
                                  │ embeds
                                  ▼
                ┌─────────────────────────────────────────┐
   wgpu  ──────▶│  Canvas GPU surface (swapchain)           │
   compute      │  • Layer textures (tiled, GPU-resident)   │
                │  • Blend/compositing graph (compute pass) │
                │  • Adjustment/filter shaders (WGSL)       │
                │  • Brush engine (stamp/spacing on GPU)    │
                │  • Selection mask channel                 │
                └─────────────────────────────────────────┘
```

**Compositing model**: each pixel layer = a set of GPU tiles (e.g. 256×256) in a texture atlas. A compositor walks the layer tree bottom-up, accumulating a backdrop, running blend-mode compute passes (Normal, Multiply, Screen, Overlay, etc.) and applying masks + clipping. **Adjustment layers and live filter layers are tree nodes**: when the walk reaches one, it runs that effect's compute pass over the accumulated backdrop (or, if clipped/nested, over just the target layer). Per-layer FX run as passes attached to their layer. Everything is recomputed from the graph, so the whole stack is **non-destructive** by construction.

**Color pipeline**: internal working space = linear float (RGBA16F or 32F) for correct blending/effects; convert to/from document ICC profile at boundaries; dither on 8-bit export.

**Why tiled**: enables >100MP documents, partial GPU residency, fast undo (dirty-tile snapshots), and bounded VRAM.

---

## 4\. Document & layer model

*   **Document**: canvas size, resolution/DPI, color space/depth (8/16/float), layer tree, metadata.
*   **Layer types (v1)**: pixel layer, group, **adjustment layer**, **live filter layer**, mask (per-layer raster mask), text layer (rasterized, kept editable as a special pixel layer with stored params), shape layer (rasterized, params stored for re-edit).
*   **Effects are layers (Affinity model).** Adjustment layers and live filter layers are **first-class nodes in the layer tree**, not just nested effects. As a sibling in the tree they apply to the **composited backdrop of everything below them in their group**; nested inside / clipped to a layer they affect only that layer (clip indicator). Either way they are movable, maskable, reorderable, toggleable tree items with their own opacity + blend mode.
*   **Two ways to attach the same effect** (both supported, both non-destructive):
    *   *As a layer* — sits in the tree, affects the backdrop below (or clipped to one layer). This is the default, Affinity-style.
    *   *As a layer FX / nested effect* — the per-layer ordered effect stack (drop shadow, glow, stroke, an inline blur) that travels with that one layer.
*   **Non-destructive stack**: effect-layer order in the tree **and** the per-layer FX list are both ordered, serialized, and recomputed in the compositor.
*   **Storage**:
    *   Native format `.sable` = container (zip-like) holding tiled layer data (compressed), the layer/effect graph (JSON/protobuf), thumbnails, ICC profile, history snapshot index.
    *   Import/export: PSD (read + best-effort write via a PSD lib/own parser), PNG/JPEG/TIFF/WebP, flatten + export presets.
    *   **Affinity** `**.afphoto**` **import**: desired, but the format is closed with no public spec — treat as a research/reverse-engineering spike, not a committed v1 feature. PSD is the realistic interop path (Affinity itself exports PSD).
*   **Undo/history**: command pattern + dirty-tile snapshots; non-destructive edits are cheap (just graph mutations). Configurable history depth, disk-backed for big snapshots.

---

## 5\. Core editing feature roadmap (Affinity-parity target)

Grouped by subsystem; each is a backlog lane, not all in v1.

**Canvas & navigation**: zoom/pan/rotate view, pixel grid, rulers/guides, snapping, multiple views.

**Selection**: marquee, lasso, polygonal, magic wand, color range, quick-mask, feather, grow/shrink, refine edge, save/load selection. _(AI selection in §6.)_

**Paint/draw**: brush engine (pressure via tablet/`RawInput`/`libinput`, spacing, jitter, dual brush, textured tips), pencil, eraser, clone stamp, healing brush, smudge, gradient, fill/bucket, pattern fill.

**Transform**: move, scale, rotate, skew, perspective, warp/mesh, free transform, content-aware scale.

**Text**: rich text layer, fonts, kerning/leading/tracking, on-path text (v1 rasterized).

**Color**: swatches, palettes, eyedropper, gamut warnings, soft-proofing.

**Workflow**: dockable panels (Dock.Avalonia), customizable workspaces, keyboard shortcuts (PS/Affinity-compatible presets), command palette, autosave/recovery, batch/macros (later), plugin API (later).

> Adjustments, live filters, layer FX, and blend modes are exhaustively enumerated in **§5A (Affinity-parity effects)**. The non-destructive + undo guarantee is in **§5B**.

---

## 5A. Affinity-parity effects — full inventory

Target: parity with everything Affinity Photo ships. **Every item below is a non-destructive, re-editable layer/effect** (see §5B). Each is its own backlog ticket; grouped, not all in v1.

### 5A.1 Adjustment layers
White Balance · Levels · Curves (RGB + per-channel + LUM) · Brightness/Contrast · Black & White · HSL Shift · Recolour · Vibrance · Exposure · Shadows/Highlights · Channel Mixer · Gradient Map · Threshold · Invert · Posterise · Colour Balance · Photo/Lens Filter · Split Toning · Selective Colour · Defringe · Apply 3D LUT · Soft Proof (OCIO/ICC).

### 5A.2 Live filter layers (re-editable, masked, stacked)
- **Blur**: Gaussian, Box, Radial (spin), Zoom, Motion, Lens/Bokeh (depth-of-field), Field (depth), Average, Median, Maximum, Minimum, Diffuse, Depth-of-Field.
- **Sharpen / detail**: Unsharp Mask, Clarity, High Pass.
- **Distort**: Twirl, Pinch/Punch, Ripple, Wave, Displace (map), Spherical, Perspective, Lens Distortion, Deform, Mesh Warp, Equirectangular/Affine.
- **Noise**: Add Noise, Denoise, Diffuse, Dust & Scratches.
- **Light / colour**: Lighting, Bloom, Glow, Shadows/Highlights, Vignette.
- **Stylize**: Halftone, Voronoi, **Procedural Texture** (formula/expression-driven — high effort, distinctive Affinity feature), Pixellate, Mosaic, Edge Detect.
- **Morphology**: Maximum, Minimum, Erode/Dilate.
- **Pro tools** (live where feasible): Frequency Separation, Lens correction (chromatic aberration + distortion), Defringe.

### 5A.3 Layer effects (FX / layer styles) — non-destructive
Gaussian Blur (FX) · Outer Shadow · Inner Shadow · Outer Glow · Inner Glow · Outline/Stroke (in/center/out) · Bevel/Emboss · Colour Overlay · Gradient Overlay · Pattern Overlay · Long Shadow · 3D (relief). Each with per-effect blend mode, opacity, and intensity, applied live in the compositor.

### 5A.4 Blend modes (full PS set + Affinity extras)
Normal, Darken, Multiply, Colour Burn, Linear Burn, Darker Colour, Lighten, Screen, Colour Dodge, Add (Linear Dodge), Lighter Colour, Overlay, Soft Light, Hard Light, Vivid Light, Linear Light, Pin Light, Hard Mix, Difference, Exclusion, Subtract, Divide, Hue, Saturation, Colour, Luminosity, **+ Affinity extras**: Average, Negation, Reflect, Glow, Contrast Negate, Erase.

### 5A.5 Per-layer compositing controls
Opacity · fill opacity · blend mode · **blend ranges / "Blend If"** (source + underlying tonal curve) · clipping to layer below · raster mask · vector mask · alpha lock · pass-through (groups) · layer color tagging.

---

## 5B. Non-destructive + undo — architectural invariant

**Hard rule:** every adjustment, live filter, layer FX, blend mode, mask, and transform is **non-destructive and re-editable at any time**, and **every** state change (including destructive pixel edits) is undoable. This is enforced by the architecture, not by per-feature discipline.

**How it's guaranteed:**

1. **The document is a graph, not a pixel buffer.** The layer tree contains pixel layers, groups, **adjustment layers, and live filter layers as first-class nodes** (§4) — plus each layer's own FX list. The on-screen image is **always a recompute** of that graph by the GPU compositor (§3). Editing an effect = mutating a node's params and re-running affected passes. Effect layers can be dragged, reordered, masked, or clipped like any layer. Nothing is ever "baked" unless the user explicitly flattens/merges/exports.
2. **Effects are parametric nodes.** A Gaussian blur stores `radius`; double-click reopens it, change radius, recomposite. Same for every filter/adjustment/FX. Reorder, toggle, mask, or delete any node anytime. Each node carries its own mask + blend mode + opacity.
3. **Two edit classes, both fully undoable:**
   - *Non-destructive* (adjust/filter/FX/blend/mask/transform/reorder) → undo = revert the graph mutation. Cheap, exact, infinitely re-editable.
   - *Destructive pixel writes* (brush, clone, eraser, paste, fill) → unavoidable for painting, but captured as **dirty-tile snapshots** in the history command (§4). Undo restores prior tiles. Bounded by tile granularity, disk-backed for large strokes.
4. **Unified history** = ordered command list over the document graph + tile snapshots. Multi-level undo/redo, **non-linear history / history states**, and named snapshots (Affinity-style). Config depth, disk-backed.
5. **Live-filter & adjustment layers persist in `.sable`** with full params, so re-editability survives save/load. Export (PNG/JPEG/flatten) is the only point where effects rasterize — and that's a copy; the editable doc is untouched.

**Consequence for implementation:** new effects MUST be authored as graph nodes with serializable params + a WGSL/compute pass + an undo entry. A destructive-only effect is a design bug. This rule is part of the effect-authoring checklist.

---

## 6\. AI subsystem — pluggable, local, user-selectable models

This is the differentiator and the hardest integration. Design goal: **the model is data, not code.** New model drops in via a manifest; no recompile.

### 6.1 Two execution tiers

| Tier | Models | Runtime | Where it runs |
| --- | --- | --- | --- |
| **Light (default, no Python)** | SAM/SAM2 (selection), U²-Net/RMBG/BiRefNet (matting/bg-remove), Real-ESRGAN/denoise, **LaMa (object removal / repair)** | ONNX Runtime (.NET) w/ DirectML / CUDA / CoreML / ROCm EPs | In-process in the .NET app |
| **Generative (opt-in)** | Diffusion: SD 1.5/SDXL/SD3, Flux.1, Flux.2, Z-Image, Qwen-Image, future | **Diffusers sidecar** (separate process) | Local sidecar over IPC, installed only on request |

The light tier ships with the app and covers selection, bg-removal, upscale, and non-generative repair — **no Python, ever**. Diffusion models change shape and tooling fast and are huge, so generative features live behind an **opt-in install** (like Affinity's optional AI download); the sidecar decouples bleeding-edge model support from the app release cycle.

### 6.2 Generative sidecar — Diffusers, opt-in install

*   **No ComfyUI.** Single diffusion backend: a thin server around **HuggingFace Diffusers** (Apache-2.0). Programmatic API, clean dep tree, covers SD/SDXL/SD3/Flux/Qwen-Image. Apache license avoids the GPL boundary entirely (see §12).
*   **Opt-in, Affinity-style.** Generative features are disabled until the user clicks **Settings → "Install generative AI"**. Light AI (selection/bg/upscale/repair) works without this.
*   **On-demand provisioning** (only when the user opts in) — app bootstraps the Python runtime with **zero manual setup**:
    *   Build an isolated venv via **`uv`** (or a pinned `python-build-standalone`) — never touch system Python.
    *   Detect GPU vendor → install the matching accelerated torch wheel (CUDA / ROCm / DirectML / MPS) + Diffusers + loaders. Progress UI, resumable, offline-cacheable.
    *   Version-lock, health-check, and a repair/reinstall + uninstall path.
*   A local process exposing a stable API (HTTP/gRPC over localhost or named pipe). The app never imports model code directly — clean boundary, swappable.
*   Sidecar lifecycle managed by the app: start/stop, health check, GPU/VRAM probe.

### 6.3 Model manager (user-provided weights only)

*   **No bundled catalog.** Users supply their own weights (license-safe, keeps app download small). Import from file/folder; optional download of a direct/HF URL the _user_ chooses.
*   **Model registry**: each model = a manifest (`model.json`): name, family (SD/Flux/Qwen/…), task(s) supported (txt2img, inpaint, upscale, segment), file paths, required VRAM, recommended params, which adapter runs it. Draft manifest auto-generated on import via heuristics; user-editable.
*   **UI**: a "Models" panel — browse installed, import, see VRAM fit vs detected GPU, set per-task default model.
*   **Per-operation model picker**: every AI action (inpaint, upscale, etc.) has a dropdown to override which installed model to use, plus param presets.
*   **Capability gating (GPU-only, no CPU path)**: if a model needs more VRAM than detected, the action is **blocked with a clear message** (suggest a smaller/quantized variant). No CPU fallback — AI requires a supported GPU. The editor still runs fully without an AI-capable GPU; AI menu items disable + explain why.

### 6.4 AI features mapped to models

| Feature | Default model class | UX |
| --- | --- | --- |
| **Smart/object selection** | SAM2 (click/box prompt) | Click subject → live mask → send to selection channel; refine + feather; one-click "select subject". |
| **Background removal** | BiRefNet / RMBG / U²-Net | One click → alpha mask as new layer/mask. |
| **Object removal / repair** | **LaMa (ONNX, ships in app)** | Paint mask → "remove". Fast, non-generative, no install. Covers most repair needs. |
| **Generative fill** | Diffusion (opt-in sidecar) | Paint mask + optional prompt → generative inpaint. Requires "Install generative AI". |
| **Generative expand / outpaint** | Diffusion (opt-in sidecar) | Extend canvas → fill new area. Opt-in. |
| **Upscale / denoise** | Real-ESRGAN / SwinIR (ONNX, ships in app) | Whole-image or layer, tiled for large images. No install. |
| **Text-to-image / generate layer** | Diffusion (opt-in sidecar) | New generated layer from prompt. Opt-in. |

### 6.5 GPU sharing concern

Editor (wgpu) and AI (ONNX/sidecar CUDA/etc.) both want the GPU. AI is GPU-only (confirmed) so contention is guaranteed when AI runs. Plan: VRAM budget manager — editor evicts non-visible tiles during heavy AI ops; sidecar reports VRAM use; UI shows a GPU/VRAM meter; AI op pre-flight checks free VRAM and blocks if it won't fit. Known constraint, not a solved problem.

---

## 7\. Solution structure (modules)

```
Sable/
├─ Sable.App            # Avalonia UI shell, DI, app lifecycle
├─ Sable.UI             # Views, panels, docking, themes, controls
├─ Sable.Canvas         # GPU canvas host, viewport, input → tools
├─ Sable.Engine         # Document model, layer tree, compositor graph
├─ Sable.Gpu            # wgpu/Vulkan binding, shaders (WGSL), compute passes
├─ Sable.Imaging        # codecs, color mgmt (lcms), tiling, I/O, PSD
├─ Sable.Tools          # brush/selection/transform tool implementations
├─ Sable.Ai             # AI orchestration: model registry, ONNX (SAM2/BiRefNet/ESRGAN/LaMa)
├─ Sable.Ai.Sidecar     # opt-in Diffusers server + provisioning (uv/venv), IPC client
├─ Sable.Format         # .sable container read/write, history
└─ Sable.Core           # shared: math, color, commands/undo, settings
```

MVVM (CommunityToolkit.Mvvm) for UI ↔ engine. Engine is UI-agnostic and headless-testable.

---

## 8\. Milestones

> Three parallel tracks (Engine / UI / AI). Milestones gate when tracks must converge.

**M0 — Spikes / de-risk (weeks 1–4)**

*   wgpu-native vs Silk.NET decision: build a spike that composites 2 tiled layers with a blend mode on GPU, embedded in an Avalonia window, on all 3 OSes.
*   Sidecar spike: app launches ComfyUI, runs one inpaint, gets result back over localhost.
*   ONNX spike: SAM2 click-to-mask in-process on each OS + EP.
*   **Gate:** GPU API chosen, sidecar IPC proven, cross-platform GPU embed proven.

**M1 — Engine skeleton (weeks 4–10)**

*   Tiled layer storage, GPU compositor (pixel layers, groups, opacity, core blend modes), viewport zoom/pan, undo via dirty tiles. Save/load `.sable`. PNG/JPEG import/export.

**M2 — Editable app (weeks 8–16)**

*   Avalonia chrome: dockable panels, layer panel, tool palette, color picker, menus, shortcuts.
*   Tools: move, marquee/lasso/wand selection, brush/eraser/fill, basic transform.
*   First adjustment layers (levels/curves/HSL). 16-bit + ICC pipeline.

**M3 — AI v1 (weeks 14–22)**

*   Model manager UI + registry/manifests + VRAM probe.
*   Smart selection (SAM2), background removal, upscale (Real-ESRGAN), all in-process.
*   Generative fill / inpaint + object removal (LaMa fast path + diffusion via sidecar), user-selectable model.

**M4 — Affinity-parity push (weeks 20–34)**

*   Full adjustment/filter set, layer FX, healing/clone, text layers, transform suite (warp/perspective), PSD import/export, autosave/recovery, soft-proofing, batch basics.

**M5 — Polish & release (weeks 32–40)**

*   Performance pass (large docs, brush latency), tablet pressure on all OSes, packaging/signing (MSIX/MSI, AppImage/Flatpak, .app/notarization), installer, docs, telemetry/crash reporting (opt-in).

_(Timeline is indicative for a small team; tracks overlap.)_

---

## 9\. Cross-platform packaging

*   **Windows**: MSIX/MSI, code signing.
*   **macOS**: `.app`, hardened runtime + notarization; MoltenVK if Vulkan path chosen (wgpu→Metal avoids this).
*   **Linux**: AppImage + Flatpak; bundle GPU runtime deps carefully.
*   **Light AI (ONNX)**: ships with the app, per-platform native ORT + execution providers.
*   **Generative sidecar**: NOT bundled — provisioned on demand when the user opts in (Settings → Install generative AI). Model weights are always user-provided.

---

## 10\. Top risks

1.  **GPU portability** — one bug surface × three drivers. _Mitigation:_ wgpu-native to collapse to one API; M0 spike on all OSes before committing.
2.  **GPU contention** (editor vs AI) — _Mitigation:_ VRAM budget manager, tile eviction, clear UX when out of VRAM.
3.  **Heavy-model footprint** — Flux.2/Qwen need lots of VRAM; many users can't run them. _Mitigation:_ quantized variants, honest VRAM gating (block, don't crash). No CPU fallback by design — light AI (ONNX) still serves users without a big GPU.
4.  **Sidecar dependency weight** (Python+torch, GB-scale) — _Mitigation:_ generative AI is **opt-in install**, not bundled; light AI ships native ONNX so the base app stays lean.
5.  **PSD fidelity** — notoriously hard. _Mitigation:_ scope to best-effort; don't block release on it.
6.  **Scope creep toward Affinity parity** — huge surface. _Mitigation:_ ruthless MVP (M1–M3), parity is M4+.

---

## 11\. Open questions for you

1.  **GPU API:** OK to commit to **wgpu-native** (one API, all platforms) over Silk.NET/Vulkan? Or prefer pure-managed even at higher porting cost?
    1.  fine
2.  **Sidecar backend:** Is bundling **ComfyUI (Python)** acceptable for max model recency, or do you want a **no-Python (GGUF/sd.cpp)** backend even if newest models arrive later?
    1.  python is fine as long as our software installs whatever is needed
3.  **Model distribution:** Should the app ship a **curated download catalog** of models, or only let users import their own weights (license-safe, lighter)?
    1.  user provided
4.  **Minimum target hardware:** What's the floor GPU/VRAM we must support (affects defaults, quantization, CPU fallback)? Any "must run on integrated GPU"?
    1.  ai features are gpu only, wont run on cpu. Does this answer question?
5.  **License/monetization:** Open-source, source-available, or commercial? Affects which libs (PSD, codecs, models) we can bundle.
    1.  Open source mit
6.  **PSD priority:** Is PSD import/export a v1 must-have or a nice-to-have?
    1.  psd and affinity import would be nice
7.  **Tablet/stylus:** Wacom/Windows-Ink/libinput pressure — v1 requirement or later?
    1.  later
8.  **Brand/name:** "Sable" placeholder — keep or change?
    1.  If u have better names im eager to hear

---

## 12\. Licensing (MIT)

App is **MIT**. Dropping ComfyUI removes the GPL-3.0 landmine entirely — the generative backend is now **Diffusers (Apache-2.0)**, which is MIT-compatible with no copyleft obligation. One item still needs a watch:

1.  **ImageSharp licensing.** SixLabors.ImageSharp v2+ is under the **Six Labors Split License** — free for OSS/small use but **commercial license required** above a revenue threshold. For an MIT OSS project it's fine, but flag it; alternatives if it bites: `Magick.NET` (Apache-2.0, native ImageMagick) or stick to SkiaSharp (BSD) + targeted native codecs.
2.  **Diffusers + torch** (Apache-2.0 / BSD) are user-installed at runtime, not bundled in our distribution — even cleaner separation.

**Other libs — quick license read:**

| Lib | License | Note |
| --- | --- | --- |
| Avalonia | MIT | ✅ |
| SkiaSharp | MIT/BSD | ✅ |
| wgpu-native | MIT/Apache-2.0 | ✅ |
| lcms2 | MIT | ✅ |
| ONNX Runtime | MIT | ✅ |
| **libraw** (RAW decode) | LGPL-2.1 / CDDL | ⚠️ Dynamic-link only to stay MIT-friendly; or make RAW an optional plugin. |
| Real-ESRGAN / SAM2 / BiRefNet weights | varies (model-specific) | User-provided → license is the user's responsibility, not ours to bundle. ✅ aligns with "user-provided weights." |

**Action:** add a `THIRD_PARTY_NOTICES` file and a license-audit step to CI before first release.

---

## 13. UI / UX layout — Photoshop/Affinity clone

Target look: dark, dense, professional, near-pixel-familiar to a PS/Affinity user. Reference screenshots map to these regions:

```
┌────────────────────────────────────────────────────────────────────┐
│ ④ Menu bar:  File  Edit  Image  Layer  Select  Filter  View  …      │
├────────────────────────────────────────────────────────────────────┤
│ ③ Context/Options bar (changes per active tool)            [Share]   │
├──┬──────────────────────────────────────────────────────┬───────────┤
│  │ ▸ doc1.sable  ▸ doc2.jpg            (document tabs)   │ ⑥ Panel   │
│② │ ┌──ruler───────────────────────────────────────────┐ │   stack:  │
│To│ │                                                   │ │  Color/   │
│ol│ │                                                   │ │  Gradient/│
│ba│ │            ① CANVAS (GPU surface)                 │ │  Swatch   │
│r │ │              rulers · guides · grid               │ │ ───────── │
│  │ │                                                   │ │  Adjust/  │
│  │ │                                                   │ │  Properties│
│  │ └───────────────────────────────────────────────────┘ │ ───────── │
│  │ [zoom%] [doc info]            [scroll]                 │ ⑤ Layers/ │
│  │                                                        │  Channels/│
│  │                                                        │  Paths    │
└──┴──────────────────────────────────────────────────────┴───────────┘
                                                  status bar / progress
```

### 13.1 Regions (mapped to the reference)

- **④ Menu bar** — File / Edit / Image / Layer / Select / Filter / View / Window / Help. Native menu on macOS, in-window on Win/Linux.
- **③ Context (options) bar** — tool-sensitive strip under the menu. Brush shows size/hardness/flow; Move shows auto-select/align; Selection shows feather/mode. Right side: Share/account.
- **② Tool palette** — narrow vertical strip, left edge. Single/double column toggle. Tool groups with fly-out for variants (lasso family, shape family). Tool tips + shortcut letters. Affinity-style "Tools" persona feel.
- **① Canvas** — the GPU surface (§3). Rulers, guides, snapping, pixel grid, multiple document **tabs**, zoom/rotate view. Bottom-left: zoom % + document dimensions/info (matches screenshot status line).
- **⑤ Layers panel** — bottom-right, the heavy one. Thumbnail, name, visibility eye, blend mode dropdown, opacity + fill sliders, lock toggles, **effect layers + FX shown as nested/indented rows** (Drop Shadow, Smart Filters, Curves, Hue/Saturation visible in ref). Add-layer/adjustment/FX/mask/group/delete buttons in footer. Tabbed with **Channels** + **Paths**.
- **⑥ Panel stack (Studio)** — dockable, collapsible panel groups: Color · Gradients · Swatches · Patterns; Adjustments · Libraries · Properties; History; Navigator; Brushes; Character/Paragraph. Each panel = tabbed, draggable, floatable, collapsible to icon rail.

### 13.2 Docking & workspace

- **`Dock.Avalonia`** for the whole shell: dockable, floatable, tabbed, auto-hide panels — matches PS/Affinity drag-to-dock behavior.
- **Saved workspaces** (Photography, Painting, default…) — user arrangements persisted + switchable, like PS workspaces / Affinity Studio presets.
- **Collapsible icon rail**: panels collapse to a thin icon strip (PS behavior).
- Floating panel windows (multi-monitor).

### 13.3 Theme & visual fidelity

- **Dark theme default** (≈ #1e1e1e / #2d2d2d panels, like the screenshots), with selectable gray levels (PS offers 4 brightness steps) + a light theme.
- Custom Avalonia control templates for: thin sliders, segment toggles, color wheel/triangle picker (ref shows HSV triangle + wheel), gradient bars, dense list rows. These are **not** stock Fluent controls — they need bespoke styling to read as pro tools.
- Icon set: custom monochrome line icons matching PS/Affinity weight. Commission or use an open icon set restyled.
- Compact metrics: small fonts, tight padding, high information density — deliberately denser than typical desktop apps.

### 13.4 Interaction parity

- **Shortcut presets**: ship Photoshop-compatible and Affinity-compatible keymaps, user-remappable. (B brush, V move, M marquee, L lasso, etc.)
- Modifier conventions (Space=pan, Alt=sample/subtract, Shift=constrain/add, Ctrl/Cmd=transient move).
- Contextual right-click menus per tool/layer.
- Command palette (search any action) — modern addition on top of the classic layout.

### 13.5 Build implication

This is a large UI surface. Treat the **UI track** as: (a) shell/docking + theme system first, (b) a reusable pro-control library (sliders/pickers/list rows), (c) per-panel implementations. The custom-control library is the long pole — budget for it in M2.
---

## 14. Toolbar & tools — researched target (PS + Affinity)

Researched from Photoshop + Affinity Photo docs (not invented). Sources: photoshopessentials tools overview, Adobe helpx tools, glensmith PS tools index, Affinity Help Center, Affinity Wiki, edits101 Affinity guide.

### 14.1 Toolbar mechanics (mirror both apps)
- **Flyout groups**: each toolbar slot shows the last-used tool; a small **triangle** marks a group with more tools. Click-hold or right-click reveals the flyout. Sable: same.
- **Context/options bar** (top, region ③): changes per active tool — brush size/hardness/flow, selection mode (new/add/subtract)/feather, fill tolerance, gradient type, type font, shape options, etc.
- **Single/double column** toggle for the strip.
- **Keyboard**: each group has a letter; **Shift+letter** cycles tools within the group.

### 14.2 Photoshop tool groups (top→bottom)
Move/Artboard (V) · Marquee: Rect/Ellipse/Single-Row/Single-Col (M) · Lasso: Lasso/Polygonal/Magnetic (L) · Object/Quick-Select/Magic-Wand (W) · Crop/Perspective-Crop/Slice/Slice-Select (C) · Frame (K) · Eyedropper/Color-Sampler/Ruler/Note/Count (I) · Spot-Healing/Healing/Patch/Content-Aware-Move/Red-Eye (J) · Brush/Pencil/Color-Replacement/Mixer (B) · Clone/Pattern Stamp (S) · History/Art-History (Y) · Eraser/Background/Magic (E) · Gradient/Paint-Bucket (G) · Blur/Sharpen/Smudge · Dodge/Burn/Sponge (O) · Pen/Freeform/Curvature + anchor edits (P) · Type H/V/Mask (T) · Path/Direct Selection (A) · Shape: Rect/Ellipse/Triangle/Polygon/Line/Custom (U) · Hand (H)/Rotate-View (R)/Zoom (Z).

### 14.3 Affinity Photo tools
View, Move, Colour Picker, Crop, Selection Brush, Flood Select, Marquees (Rect/Ellipse/Column/Row), Freehand Selection, Flood Fill, Gradient, Paint Brush, Colour Replacement, Pixel, Paint Mixer, Erase/Background-Erase/Flood-Erase, Dodge/Burn/Sponge, Clone, Undo Brush, Blur/Sharpen/Median/Smudge, Healing/Patch/Blemish/Inpainting/Red-Eye, Pen, Node, Shapes, Artistic/Frame Text.

### 14.4 Sable build order (incremental, each verifiable)
1. **Tool framework** — active tool, pointer routing in doc space, options bar binds to active tool. Reroute existing brush/eraser/fill/eyedropper. ← in progress
2. **Move** (V) — non-destructive layer offset.
3. **Marquee selection** (M) — rect/ellipse → selection; ops (brush/fill/delete) honor it; marching-ants overlay. **GIMP-style editable selection**: after drawing, show in-canvas grips — drag inside to move, corner/edge handles to resize — before committing. Then **Lasso** (freehand mask), **Magic Wand** (color range).
4. **Zoom (Z) / Hand (H)** explicit tools (wheel/middle-drag already exist).
5. **Eyedropper (I)** as a first-class tool (Alt-pick already works).
6. **Gradient (G)** — linear/radial fill.
7. **Crop (C)**.
8. **Clone Stamp (S)**, **Heal/Spot/Patch (J)**.
9. **Dodge/Burn/Sponge (O)**, **Blur/Sharpen/Smudge** brushes.
10. **Shapes (U)** + **Type (T)** + **Pen (P)** — vector-ish (rasterized in v1 per §1).
Flyout grouping + options bar + Shift-cycle land alongside as the strip matures.

### 14.5 Required UX details (tracked, currently missing)
- **Transform: non-uniform scale** — corner drag is uniform by default; holding a modifier (e.g. Shift, or Ctrl) should allow free/non-uniform scaling per-axis; edge handles scale a single axis.
- **Brush cursor preview** — show a live circle outline at the cursor sized to the brush radius for Brush AND Eraser (and any brush-based tool), so the user sees the affected area/what-will-happen before clicking. Must be drawn in the GPU overlay (blit), since the native canvas surface sits above Avalonia (airspace). Follows the cursor; reflects radius (and hardness ring later).
- **Toolbox button groups + flyout** — group related tools into one slot (e.g. Marquee group, Lasso group, Brush/Pencil, Eraser variants, Shapes), matching PS/Affinity. The slot shows the last-used tool with a small **triangle**; click-hold / right-click opens a **flyout to the side** to pick another tool in the group. **Hotkey behaviour**: pressing the group's letter selects it; pressing it again while already active **cycles** through the group's tools (Shift+letter also cycles). Current flat one-button-per-tool strip is a placeholder to be replaced by this grouped model.

---

## 15. Implementation status (canonical — update as work lands)

**As of this checkpoint.** Milestone position: **M1 complete; mid-M2 (tools); much of M4 (effects) pulled forward; M3 (AI) parked by choice.** ✅ done · 🔶 partial · ⬜ not started.

### Build / run / test
- `dotnet build` (whole solution, `Sable.slnx`). net10.0, Avalonia 12.0.4, Silk.NET.WebGPU 2.23.0. Windows-only so far.
- `dotnet test tests/Sable.Tests` — **53 xUnit tests** (pure logic; no GPU). Add tests here for new pure logic, NOT in the spike.
- `dotnet run --project src/Sable.Gpu.Spike` — GPU smoke (wgpu compute + N-layer compositor → `spike_out.png`/`m1_export.png`). Verify GPU paths here (can't unit-test WGSL).
- Run app: `dotnet run --project src/Sable.App` (or run `bin/Debug/net10.0/Sable.App.exe`; do NOT `-o` build — breaks Avalonia XAML precompile).

### Architecture ✅
- GPU-first canvas: wgpu swapchain embedded in Avalonia via `NativeControlHost` (`Sable.Canvas/GpuSurfaceControl`), surface **non-activating** (keyboard stays with Avalonia window). Windows HWND only.
- Document = layer **tree** (not flat), recomputed every frame by the GPU compositor. Engine UI-agnostic; MVVM (CommunityToolkit) UI.

### Engine / compositor ✅
- `GpuCompositor` recursive `CompositeList` (groups), ping-pong scratch pool per depth, present-copy compute (no row-align limit), CPU readback for export.
- Layers: **PixelLayer** (tiled 256² for undo), **GroupLayer** (isolated grouping), **AdjustmentLayer**, **FilterLayer**. Per-layer: opacity, blend mode (7: Normal/Multiply/Screen/Overlay/Darken/Lighten/Add), **mask** (R-channel, src-over/adjust), **clip-to-below**, **affine transform** (offset/scale/rotation, inverse-affine bilinear sample).
- Partial GPU upload: per-layer `DirtyTiles` → only changed tiles re-uploaded (mask still full-upload 🔶).

### Effects (M4) — all non-destructive, masked, serialized
- Adjustments ✅: Brightness/Contrast, Levels, HSL, Curves (unified `adjust.wgsl`, generic params + curve LUT). More (ColourBalance/Vibrance/etc.) ⬜.
- Live filters 🔶: Gaussian blur ✅ (separable, `blur.wgsl`). Filter mask/opacity ⬜ (blur applies fully). Sharpen/others ⬜.
- Recipe: new adjustment = AdjustmentKind + adjust.wgsl case + PackParams + toolbox sliders + serializer; new filter = FilterKind + WGSL + compositor branch + serializer.

### Tools (M2, PLAN §14) — `ToolKind`, routed in `GpuSurfaceControl.Input.cs` (Win32 WndProc)
- ✅ Move (layer offset + bounds overlay), Transform (gizmo: rotated box + corner uniform-scale + rotate handle + move), Brush, Eraser, Fill (flood, tolerance), Eyedropper (also Alt+click), Hand (pan), Zoom (wheel=zoom-to-cursor, middle-drag=pan), Marquee (rect selection).
- ✅ Toolbox: grouped slots (V=Move/Transform, B=Brush/Eraser), **hover flyout** (managed Popup, grace-close timer keeps it open while travelling button→flyout, auto-swaps on hovering another slot), hotkey **re-press cycles**, highlight + options-bar tool name.
- ✅ Brush preview: dab stamped into a layer copy pre-composite (erase reveals below; respects blend/opacity) + cursor ring.
- ✅ Selection: rect marquee, marching-ants overlay, honored by brush/fill/Delete; Esc/Ctrl+D deselect.
- ✅ Selection **grips** (GIMP-style): with rect Marquee active, 8 white handles drawn on the selection (shader); drag a corner/edge handle to resize, interior to move (clamped in-bounds), empty area starts a new selection. `HitSelHandle` in `GpuSurfaceControl.Input.cs`.
- ✅ Non-rect selections via per-pixel mask (`Document.SelectionMask` doc-sized 0/255; `Selections` builders, headless-tested): **Ellipse** (M flyout, rubber-band → inscribed ellipse), **Lasso** (L, freehand polygon, even-odd fill), **Magic Wand** (W, contiguous-color flood on active layer). All clip Brush/Eraser/Fill/Delete via `Brush.ClipMask` + `FillTool.Flood(mask)` + masked delete. `Document.SetMaskSelection`/`ClearSelection`.
- 🔶 Selection overlay: rect = marching ants + grips; mask selections (ellipse/lasso/wand) currently show **bounding-box** ants only. (Mask-edge outline was implemented — R8 mask texture + `textureLoad` edge-detect in blit — but **reverted** after a startup crash; the crash was an out-of-bounds `stackalloc` write, not the GPU design, so this can be safely retried with a `[4]`-sized bind-group-layout stackalloc.)
- ⬜ Selection polish: not serialized to `.sable` (transient), no add/subtract/intersect modes, no feather/grow/shrink/refine; mask-edge overlay (retry).
- 🔶 Transform: uniform scale only (non-uniform/edge handles ⬜). Move bounds overlay axis-aligned.
- 🔶 **Gradient (G) — REPORTED INCORRECT, revisit**: scaffolding in place but behaviour is wrong (user-flagged; specifics TBD). `GradientTool.Apply` (multi-stop ramp via `GradientDef`/`GradientStop`/`Sample`) along the drag line, src-over into active layer, selection-clip + feather, undoable; G = Fill/Gradient flyout; live drag-line overlay (blit uniform 192B); Gradients-tab editor (`GradientBar`: draggable stops, click-add, +/− , colour-wheel-per-stop, mutates `Canvas.Gradient`). NEEDS DEBUG before marking done — verify: stop interpolation, drag-line→fill mapping, tab/wheel routing, paint result vs preview.
- ✅ **Crop (C)** + **document resize model**: `Document.SetSize`/`PixelLayer.SetBuffer`/`RasterTiles.Crop`. `CropCommand` (undoable) rebuilds all pixel layers + masks to the crop region (recurses groups), preserves offsets/transforms. Crop tool: drag rect → dim-outside + border overlay (`BlitOverlay.CropOn`) → **Enter** commits + refits view, **Esc** cancels.
- ✅ **Resize Document** (Image ▸ Resize Document…): `ResizeCommand` (undoable) premult-bilinear/nearest **resample** of every layer + mask (`RasterTiles.Resample`) → scales content; proportional offset scale, `Document.Dpi`. Dialog `ResizeDocumentWindow` (aspect-link, units(px), DPI, resample method + on/off). Follow-up: bicubic/Lanczos, cm/inch units.
- ✅ **Resize Canvas** (Image ▸ Resize Canvas…): bounds-only, **no resample** — layers keep pixel size; grow pads transparent / shrink crops, 9-point **anchor** (`ResizeCanvasWindow`). Reuses `CropCommand` (anchor → crop origin; negative origin grows). The two are deliberately distinct: Document=resample/scale, Canvas=bounds-only.
- ✅ **Shapes (U)** — parametric: `ShapeLayer` (kind + rect + fill + stroke, no baked pixels) rendered live by the compositor (`GetShapeBuffer`, re-rasterize on dirty). Rectangle/Ellipse filled, Line stroked, AA. Each drawn shape = its own auto-selected layer; **editable fill** (colour wheel recolours the selected shape, routed via `_shapeTarget`); **tight bounds** via `Layer.ContentBounds` so **Move (V)** hugs the shape (Move now works on any layer type via `Canvas.SelLayer`, not just pixel). Live drag outline overlay. Serialized in `.sable` ("shape"). Follow-up: stroke colour/fill toggle in options bar, Transform on shapes, polygon/star kinds, true vector (non-raster) export.
- ✅ **Clone stamp (S)**: `BrushTool` clone path samples colour from a source buffer at a locked offset (honors coverage + clip/mask). Alt+click sets source; paint copies (source = layer snapshot at stroke start → no feedback). Undoable via brush stroke. Follow-up: cursor ring + source crosshair overlay, cross-layer source.
- ✅ **Type (T)** — parametric `TextLayer` (string/size/colour/font/bold/italic/pos) rasterized live via SkiaSharp (`Sable.Imaging/TextRaster`), compositor `GetTextBuffer`, tight `ContentBounds` (Move hugs text). **On-canvas editing**: click places an editable text + caret (drawn in blit), window `OnTextInput` feeds chars live, Backspace/Enter/Esc; click existing text re-edits; tool shortcuts suppressed while typing. **Font controls** in options bar: family combo (`TextRaster.Families`), Bold/Italic, size; colour wheel recolours. Serialized ("text", incl. font/style). Caveats: single-line, caret at end only. Follow-up: multi-line/wrap, mid-text caret, alignment.
- ✅ **Retouch brushes (O)**: `BrushTool.Mode` (`BrushMode`) transforms pixels under the dab × coverage × `Strength` — Dodge (lighten) / Burn (darken) / Sponge (desaturate→luminance) / Blur+Sharpen (3×3) / Smudge (carries colour, `BeginStroke` resets). O group cycles all six; cursor ring; undoable via stroke→`PaintRasterCommand`; honors selection. Follow-up: per-mode strength slider, shadows/mid/highlights range for dodge/burn.
- ⬜ heal/spot/patch, pen/node.

### IO ✅
- `.sable` save/load (zip: `document.json` + `layers/{i}.raw` + masks; recursive groups; all layer params incl transform/clip/adjustment/filter). PNG import (as new doc) + export (flattened). Open/Save/SaveAs/Export + Ctrl+O/S, Z/Y undo.

### UI ✅/🔶
- Dark PS/Affinity chrome: menu, options bar, tool strip, canvas (tabs/status), Color panel (ColorView), Layers panel (tree, indent, visibility/blend/opacity/clip, add/delete/reorder/group/ungroup, **multi-select**, **drag-drop**: onto sibling=auto-group, onto group=move-in). Modeless **Adjustment/effect toolbox window** (opens on effect-layer select, centered over canvas).
- ✅ **Affinity-style layer rows** (reworked): `[thumb|type-icon] [name] … [clip] [eye-right]`. Pixel layers = **live downscaled thumbnail** over checker (`LayerViewModel.BuildThumb`, box-average; refreshed in VM ctor/resync + after each `CommandProduced` paint/fill/erase/delete). Adjustment=sliders / Filter=droplet / Group=folder SVG glyph. Visibility = eye toggle on far right; clip-to-below = corner-arrow icon. **Group disclosure chevron** (collapse/expand hides children; collapse state in `DocumentViewModel._collapsed`, transient). Nesting guide on indented rows. Dense **blue selection bar** (`ListBox.layers > ListBoxItem` styles). Footer buttons + row glyphs now **SVG line icons** (`Path.icon`/`Button.iconbtn`/`ToggleButton.eye` in App.axaml) — no emoji.
- ✅ **Tool-specific options bar**: `UpdateOptionsBar(kind)` shows only the panels relevant to the active tool (SizeOpts / SelectOpts feather / TypeOpts fonts / MaskHint). Text tools: family combo + B/I/U/S + size + align(L/C/R) + line-spacing; multiline (Shift+Enter); on-canvas caret per-line.
- ✅ **Custom colour picker**: reusable `ColorPicker` UserControl = bespoke `ColorWheel` (hue ring + rotating sat/val triangle, draggable, antialiased; PLAN §13.3) + hex field + H/S/V readout, self-contained (`Color`/`SetColor`/`ColorChanged`). Replaced stock Avalonia `ColorView`; drives brush / selected shape+text recolour / gradient stop / eyedropper. Right panel rows `Auto,Auto,*` so Color sizes to content, Layers fills rest. (RGB only — alpha slider is a follow-up.)
- ✅ **Unified control theming**: all chrome controls 24px, dark `#2B2B2B` fields / `#3C3C3C` borders / `#5687C0` accent. Slider = stock Fluent Track + small custom **thumb template** (14px ellipse, no clip) + `-4` top margin to vert-center with row text (the Fluent thumb ignores `Width`; only its template override shrinks it). Numeric fields = plain `TextBox` (NumericUpDown spinner wouldn't hide/fit at 24px — `ToggleButton.opt` compact toggles, blue when checked). **Gotcha: verify chrome visually — `Start-Process` screen-capture here is unreliable (foreground/SendKeys miss); trust the user's eyes.**
- ✅ **Tool strip = SVG line icons** (`WireTools` `MakeIcon` builds a `Path` per button from Lucide-style geometry; main + flyout + on-change all use it). No emoji anywhere in `Sable.App` (verified by unicode grep — only `→` arrows in comments).
- ✅ **Pro-tool control restyle (first pass)**: thin recolored sliders (Fluent resource overrides — track 3px, blue value fill, small `Thumb`), compact dark combo boxes + checkboxes (App.axaml). Gotcha logged: Fluent `Slider*ContentMargin` keys are `GridLength` not `Thickness` (wrong type = startup `InvalidCastException`).
- 🔶 Panels are static layout (no `Dock.Avalonia` docking yet). Group composite thumbnail not rendered (folder icon used). Slider thumb shrink via `/template/ Thumb` may not fully take (theme-dependent) — verify live.

### Render perf (M1 follow-up, DONE — verified by per-frame instrumentation, since stripped)
- **Frame pacing 30→60fps**: the canvas render `DispatcherTimer` ran at default **Background** priority and was starved (idle ~30fps + 100–240ms freezes). Now `new DispatcherTimer(8ms, DispatcherPriority.Render, …)` + `timeBeginPeriod(1)`. Locked ~60fps.
- **Hover no longer full-recomposites**: brush/eraser hover set a preview dab every frame → full-doc recomposite. Now recomposite only when `NeedsComposite` OR the preview dab actually changed (`!Nullable.Equals(dab,_lastPreview)`); stationary hover reuses the last composite.
- **Surface-outdated freeze**: opening an image left the canvas blank until a manual maximize. Cause: the file dialog occluding the window marked the wgpu surface `Outdated`; `RenderFrame` silently `return`ed every frame forever. Fix: on `SurfaceGetCurrentTextureStatus.Outdated`/`Lost`, call `Configure(_width,_height)` and recover next frame.
- **Still open (large-doc paint)**: each brush *move* still recomposites the whole document (~8ms @ 3.6MP → ~38ms @ 17MP). Real fix = composite caching (cache backdrop below active layer, re-blend only active+dab) or region compositing. Acceptable now at 60fps base; revisit for very large docs.

### Cross-platform (in progress)
- **Platform backend seam (DONE)**: `Sable.Canvas/Platform/IPlatformBackend` is the single seam for OS-specific canvas code — `CreateSurface(handle)` (native window → wgpu surface), `CreateInput()` (native event source), `RaiseTimerResolution()`. `CanvasPlatform.Current` selects once: `WindowsBackend` (real) vs `UnsupportedBackend` (Linux/macOS stub — surface throws `PlatformNotSupportedException`, caught in `InitGpu` → blank canvas, no crash; `NullInputSource` = no input; shared engine/UI still run). **Everything else (render loop, compositor, viewport, tool logic, coordinate mapping) is platform-agnostic** — adding an OS = one new backend.
- **Input seam (DONE)**: mouse/keys go through `IInputSource` → shared `ICanvasInputSink` (normalized surface coords + `CanvasMods`). `WindowsInputSource` = the WndProc subclass (decode-only); ALL tool logic moved to `GpuSurfaceControl`'s sink impl (`PointerDown/Move/Up/Wheel`), identical on every OS. No Win32 P/Invoke left in `GpuSurfaceControl` — only in `Platform/Windows*`.
- **Selection combine modes (DONE)**: Shift=add / Alt=subtract / Shift+Alt=intersect / none=replace, across rect/ellipse/lasso/wand (`Selections.Combine`, `Document.SnapshotSelectionMask`).
- **Mask-edge overlay (DONE)**: ellipse/lasso/wand now show marching ants along the TRUE coverage edge, not a bbox. `Document.SelectionVersion` → `GpuSurfaceControl` uploads the coverage mask to an R8 texture (`UpdateSelMaskTexture`, 256-aligned `QueueWriteTexture`); `fullscreen_blit.wgsl` binding 3 edge-detects it. (The earlier revert was an OOB `stackalloc`, fixed — bind arrays sized `[4]`.) Rect marquee still uses bbox ants + grips.
- **Feather (DONE)**: `Selections.Feather` (separable box blur of the coverage mask); options-bar Feather slider → `GpuSurfaceControl.SelectionFeather`, applied on commit in `ApplyMask`. `BrushTool` now multiplies by clip-mask coverage (soft edges). 68 tests. NOTE: fill/delete still treat the selection as binary (feathered fill/delete edge = follow-up).
- ⬜ Real Linux (Xlib/Wayland) + macOS (CAMetalLayer) backends: implement `IPlatformBackend.CreateSurface` (build the OS wgpu descriptor from Avalonia's handle) + an `IInputSource` for that OS. The shared canvas/tool code is ready.

### Requested backlog (user-prioritised)
- ✅ **Multi-document tabs (DONE, Phase 2 #1)**: `DocumentTab` (Document + own `DocumentViewModel`/undo + path/title/dirty/active); `MainWindow` `_tabs` `ObservableCollection` + tab strip (`TabStrip` ItemsControl, click-switch, × close, + new); `ActivateTab` swaps `Canvas.Document` + `DataContext` + rewires canvas callbacks (`WireCanvas`); `OpenInNewTab` for New/Open/OpenImage/drop. Close prompts on dirty (`ConfirmWindow`). Ctrl+N/Ctrl+W. Done with all sub-requirements:
  - ✅ **Start with NO active image** — demo removed; `EmptyState` welcome overlay until New/Open/paste/drop.
  - ✅ **New dialog** (`NewDocumentWindow`) — size + DPI + presets (Square/HD/4K/A4/IG).
  - ✅ **New from clipboard** — File▸New from Clipboard → `ReadOsImage` (`TryGetBitmapAsync`) → new tab sized to it.
  - ✅ **Drag-and-drop image / `.sable` files** → window-chrome `DragDrop` (Avalonia 12 `e.DataTransfer.TryGetFiles()` / `DataFormat.File`) → one new tab per file.
- ✅ **Export dialog (DONE, Phase 2 #2)**: File▸Export… (`ExportDialog`) — format **PNG / JPEG / WebP** + quality (lossy) + scale% with live **preview** + **estimated size**; `ImageCodec.EncodeScaled(fmt, src, rgba, outW, outH, quality)` (SkiaSharp resize; JPEG flattened over white) + `ImageCodec.ImageFormat`/`Extension`; `DocumentIO.Export`. MainWindow composites (`ReadComposite`) → dialog → save picker (per-format extension). Replaced the direct Export-PNG. (TIFF not offered — SkiaSharp can't encode it.)
- ✅ **Brush hardness + flow UI** — Hardness + Flow sliders in the brush options bar (`OnBrushHardnessChanged`/`OnBrushFlowChanged` → `Brush.Hardness`/`Flow`).
- ✅ **Affinity HUD brush adjust** — **Ctrl+Alt + left-drag** on canvas: horizontal = size, vertical = hardness (`_hudAdjust` branch in `GpuSurfaceControl` input, intercepts before painting; live preview ring; `BrushAdjusted` event → `SyncBrushSliders`).
- ✅ **Pencil** (B group) — `Brush.Pencil` → hard binary coverage (no antialias/falloff). ✅ **Eyedropper options** — sample size Point/3×3/5×5 (`EyedropperRadius`) + All-layers (`EyedropperAllLayers` samples the composite); options-bar panel.
- ⬜ **Gradient radial/conical/reflected** — deferred (gradient tool flagged incorrect by user; revisit later).
- **About dialog**: Help▸About — app name/logo, version (assembly/informational version), build, MIT licence + `THIRD_PARTY_NOTICES`, links (site/repo/releases), copyright. Small modal.
- **Update check + auto-update** — model after Novalist (`E:\git\novalist-official` `Novalist.Core/Services/UpdateService.cs`): **custom, GitHub-Releases-based, no third-party framework** (no Velopack/Squirrel/Sparkle). `Sable.Core` (or `Sable.App`) `UpdateService` + `IUpdateService`: `CheckForUpdateAsync` polls the repo's `releases/latest` GitHub API → `UpdateInfo` (version/tag/html-url/body/asset download-url+name+size); compares to current version; picks the **OS/arch-matched asset** (RuntimeInformation, seam-injected for tests). `DownloadUpdateAsync` → `%LocalAppData%/Sable/Updates` with progress + cancel. `LaunchInstaller` runs it (per-OS: MSI/MSIX, AppImage, .app). UI: "update available" prompt (version + release notes/body) → download progress → install/relaunch; Help▸Check for Updates; settings toggle for auto-check on launch. Cross-platform asset naming per OS.
- **GitHub Actions CI/CD** — port from Novalist (`E:\git\novalist-official/.github/workflows`), **minus the SDK build + `Sdk.Example` extension steps**:
  - **`ci.yml`**: `dotnet build` + `dotnet test` (xUnit) on push/PR + coverage badge (`eng/Check-Coverage.ps1` / `Publish-CoverageBadge.ps1`). Drop the separate `Novalist.Sdk` test/coverage rows; keep Core/Engine/Tools/App-equivalent.
  - **`release.yml`**: tag-triggered, **per-OS matrix** (windows-x64, macos-x64, macos-arm64, linux-x64) → `dotnet publish -p:PublishSingleFile=true` → package per OS: **Windows = Inno Setup `.iss` installer** (`.github/installers/windows/`), **macOS = ad-hoc-signed `.dmg`** (`Info.plist.template`, Gatekeeper), **Linux = AppImage**; upload as release assets (these are exactly the assets the §15 `UpdateService` downloads). Reuse Novalist's installer scripts/templates, renamed Sable. Code-signing/notarisation = later (M5).
- _(more to come — list is open.)_

### Known gaps / debt
- M3 AI ⬜. HiDPI assumes 1:1. Doc-swap leaks old GPU layer buffers. No ICC/16-bit-export, no history panel/non-linear undo UI, no `Dock.Avalonia`. Selection: not serialized to `.sable`; fill/delete treat feathered edge as binary. Large-doc paint still full-recomposites per brush move (composite-cache follow-up). Colour picker is RGB-only (no alpha slider). Gradient tool flagged incorrect (revisit deferred).

### Key files (orientation)
- Compositor: `src/Sable.Engine/Compositing/GpuCompositor.cs` + `src/Sable.Gpu/Shaders/*.wgsl` (composite/adjust/blur/stamp/present_copy/fullscreen_blit).
- Layers/commands: `src/Sable.Engine/Layers/*`, `src/Sable.Engine/Commands/LayerCommands.cs`, `Document.cs`, `AffineMath.cs`, `SelRect.cs`.
- Canvas/tools/input: `src/Sable.Canvas/GpuSurfaceControl*.cs`; tools `src/Sable.Tools/*` (BrushTool/FillTool/StrokeSession/PaintRasterCommand/ToolKind).
- Format: `src/Sable.Format/SableFile.cs`. App/UI: `src/Sable.App/MainWindow.axaml(.cs)`, `AdjustmentWindow`, `src/Sable.UI/ViewModels/*`.

---

## 16. Feature gap analysis — vs Photoshop / Affinity Photo (for review)

Researched against Photoshop + Affinity Photo. Legend: ✅ have · 🔶 partial · ⬜ missing. Review backlog, not a commitment — grouped to prioritise.

### 16.1 Document / canvas / navigation
- ✅ open (PNG/JPEG/WebP), `.sable` save/load, crop, resize document (resample), resize canvas (anchor), zoom/pan, pasteboard.
- ⬜ multi-document tabs · new-document dialog (presets/size/DPI/colour-space/background) · rotate canvas/view · rulers · guides (+manager) · grid · snapping (guides/grid/layers/pixel) · multiple views · navigator (placeholder) · flip canvas H/V · artboards · recent-files/templates.

### 16.2 Selection & masking
- ✅ marquee rect/ellipse, lasso, magic wand, add/subtract/intersect, feather, true-edge ants, grips, delete, deselect.
- ⬜ polygonal lasso · magnetic lasso · selection brush · colour-range select · grow/shrink/smooth/border · refine-edge / select-and-mask (hair) · quick mask (Q) · save/load selection · invert selection · feathered fill/delete (now binary) · transform selection. 🔶 not serialised.
- ✅ **Clipboard: Copy / Cut / Copy Merged / Paste / Paste Into / Duplicate** — `SableClipboard` (process-internal, region or whole-layer; shared across tabs) **+ OS clipboard image** (Avalonia 12 `ClipboardExtensions.SetBitmapAsync`/`TryGetBitmapAsync` in `Avalonia.Input.Platform`). Copy = selected region of the active layer (masked, bounds) or whole layer if no selection → also written to the OS clipboard as a bitmap; Copy Merged (Ctrl+Shift+C) = composite cropped to selection; Cut = copy + `DeleteSelection` (undoable) or copy+delete layer; Paste = internal region/layer, else OS bitmap → new pixel layer (centred); Paste Into (Ctrl+Shift+V) = region + selection as its mask; **Duplicate (Ctrl+J)** = deep `Layer.Clone()` inserted above (undoable). Edit menu + Ctrl+C/X/V/J shortcuts. `Layer.Clone()`/`CreateClone()` per type (pixels/params/mask/effects/children deep-copied).

### 16.3 Layers & compositing
- ✅ pixel/group/adjustment/filter/shape/text, opacity, 7 blend modes, raster mask, clip-to-below, non-destructive transform, reorder, multi-select, drag-drop, group/ungroup, thumbnails.
- ✅ **full blend-mode set** (30). ✅ **fill opacity**. ✅ **duplicate** (Ctrl+J). ✅ **merge-down / merge-visible / stamp / flatten / rasterise** — GPU-collapse: `GpuSurfaceControl.RenderLayersToPixels(layers)` (temp Document → `CompositeToBytes`) → new `PixelLayer`, swapped via `ReplaceLayersCommand`; Layer menu + Ctrl+E / Ctrl+Shift+E / Ctrl+Shift+Alt+E.
- ✅ **locks (position / pixels / alpha)** — `Layer.LockPosition/LockPixels/LockAlpha`; enforced in input (Move gated, paint/fill blocked, brush `LockAlpha` preserves alpha); panel toggles; serialized. ✅ **colour tags** (`Layer.ColorTag` 0-7; row strip + 8-swatch picker in panel header; serialized). ✅ **between-row drop reorder + indicator** — `DocumentViewModel.DropLayerRelative(dragged, target, above)` (index-corrected `MoveLayerToCommand`); drag shows a blue insertion line; group-middle band still drops into the group.
- ⬜ Blend-If · vector mask · multi-layer clipping · pass-through groups · smart objects · layer search.

### 16.4 Adjustments (have 14 of ~22)
- ✅ **Exposure** · **Vibrance** · **Threshold** · **Posterise** · **Invert** · **Black & White** · **White Balance** · **Shadows/Highlights** (tonal lift/recover) — `adjust.wgsl` cases 4-10,13; ≤6 params, gradient-slider panels, serialized.
- ✅ **Colour Balance** (shadow/mid/highlight RGB shifts) · **Channel Mixer** (3×3 matrix) — cases 11-12, 9 params each. **Adj uniform buffer expanded 32B→64B** (`_adjParamsBuf`, `prm` stackalloc uint[16], `DispatchAdjust` Size=64, `adjust.wgsl` Adj struct p0..p11) — now any adjustment up to 12 params fits.
- ✅ Brightness/Contrast, **Levels** (in black/white/gamma + **output black/white** remap), HSL, **Curves** (RGB composite + per-channel R/G/B; 4×256 GPU LUT bound to `adjust.wgsl` binding 5, `AdjustmentLayer.BuildLut`/`EvalChannel` monotone Catmull-Rom; bespoke `CurveEditor` control in the AdjustmentWindow — click=add point, drag=shape, right-click=delete, RGB/R/G/B channel tabs; serialized as point lists in `.sable`).
- ✅ **Affinity-style adjustment panel** (`AdjustmentWindow`): header + **Reset** button, **gradient slider tracks** (`GradientSlider` control — hue rainbow / sat ramp / grey for levels), numeric **value box** beside each slider, **histogram** behind Curves (`CurveEditor.SetHistogram`) + above Levels (`HistogramView`) via `Histogram.Compute/Draw` (fed by `MainWindow` `CompositeProvider = Canvas.ReadComposite`; **whole-composite approx — backdrop-below is a follow-up**), footer **Opacity + Blend Mode**.
- ⬜ LUM-channel curve · White Balance · Black&White · Recolour · Vibrance · Exposure · Shadows/Highlights · Channel Mixer · Gradient Map · Threshold · Invert · Posterise · Colour Balance · Photo/Lens Filter · Split Toning · Selective Colour · Defringe · 3D LUT · Soft Proof · presets · adjustment brush · on-canvas handles.
- **Curves recipe note**: an adjustment needing a LUT (vs 6 scalar params) uses the `_curveLutBuf` storage binding + `adj.Kind` switch case in `adjust.wgsl`; reuse this for Gradient Map.

### 16.5 Live filters (have 10)
- ✅ Gaussian blur · **Box · Motion · Zoom blur · Sharpen · Unsharp Mask · High Pass · Clarity · Add Noise · Denoise** (`FilterKind` 0-9). Each filters the backdrop into a scratch (`RenderFilter`) then `BlendBufferInto` with the layer's **opacity + mask** — ✅ **filter mask/opacity closed**. Shaders: `blur.wgsl` (gaussian + box flag), `filter_dir.wgsl` (motion/zoom), `filter_conv.wgsl` (sharpen), `filter_combine.wgsl` (unsharp/high-pass/clarity, 2-input), `filter_noise.wgsl` (add-noise/bilateral-denoise). Toolbox shows Radius/Amount/Angle per kind; Filter menu + footer flyout; serialized (`FilterAmount`/`FilterAngle`). GPU smoke = one of every kind in `Sable.Gpu.Spike`.
- ⬜ Radial/Lens/Field blur · Average/Median/Min/Max · distort (Twirl/Pinch/Ripple/Wave/Displace/Spherical/Perspective/Mesh-Warp) · Dust&Scratches · Lighting/Bloom/Glow/Vignette · stylise (Halftone/Voronoi/Procedural-Texture/Pixellate/Mosaic/Edge-Detect) · morphology · Frequency Separation · lens correction.

### 16.6 Layer effects / FX (have 8)
- ✅ **Drop Shadow · Outer Glow · Stroke · Colour Overlay · Inner Shadow · Inner Glow · Gradient Overlay · Bevel/Emboss** — per-layer non-destructive `Layer.Effects` stack (`LayerEffect`), rendered by the compositor (`BlendContentWithFx`): shadow/glow behind, the rest in **Effects-list order** in front (reorder changes stacking). `fx.wgsl` modes 0 tint · 1 stroke · 2 inner-shadow · 3 inner-glow · 4 gradient · 5 bevel (alpha-edge lighting) — 48B fx params; outer shadow/glow reuse `blur.wgsl`; sprites via `BlendBufferInto`. Dedicated modeless **Layer Effects dialog** (`EffectsWindow`, footer **fx** button / Window▸Layer Effects) — Affinity master-detail (effect list + per-effect blend-mode combo, colour hex + live swatch, sliders, **Move Up/Down reorder**). Serialized in `.sable` (`EffectDto`). GPU smoke in `Sable.Gpu.Spike` (all 8).
- ⬜ Pattern Overlay · Long Shadow · 3D (Affinity-specific, **out-of-scope**). Full drag-reorder of a free-form effect stack = later (Up/Down buttons cover reorder for now).

### 16.7 Painting / brushes
- ✅ soft round brush, eraser, flood fill, multi-stop linear gradient, clone, dodge/burn/sponge, blur/sharpen/smudge, preview+ring, size, strength.
- ⬜ hardness+flow UI (fields exist) · brush HUD adjust (Ctrl+Alt-drag) · brush presets panel · textured/image tips, spacing/jitter/scatter/dual/rotation/wet-edges · tablet pressure/tilt (post-v1) · pencil · colour-replacement · paint-mixer · pattern stamp · symmetry/mirror · stabiliser · gradient types beyond linear (radial/conical/reflected).

### 16.8 Retouching / repair
- ✅ clone stamp.
- ⬜ healing brush · spot healing · patch · inpainting/content-aware (LaMa, M3) · blemish/red-eye · history/undo brush · perspective clone.

### 16.9 Transform / distort
- ✅ move (offset), free transform (uniform scale + rotate).
- ⬜ non-uniform scale + edge handles · skew/shear · perspective/distort · warp/mesh warp · content-aware scale · puppet/liquify · numeric transform panel · transform-again · align/distribute.

### 16.10 Vector / shapes / text
- ✅ rect/ellipse/line (parametric), text (parametric, font/B/I/U/S/align/leading, multiline, on-canvas edit).
- ⬜ pen/bezier + node editing · more shapes (polygon/star/arrow/custom) · shape stroke (width/colour/dash/joins)+fill toggle · boolean ops · text-on-path · frame/area text+wrap · character/paragraph panels (kerning/tracking/super-sub/lists) · text styles · text→curves · vector export.

### 16.11 Colour
- ✅ custom HSV wheel+triangle, hex, eyedropper.
- ⬜ swatches/palettes (placeholder) · alpha in picker · RGB/HSL/CMYK/LAB sliders · fg/bg swatches+swap · gradient presets · patterns · gamut warning/soft-proof · colour sampler points · histogram (placeholder) · info panel · 16/32-bit + ICC pipeline (internal working space exists; no UI/convert).

### 16.12 File / IO / export
- ✅ `.sable`, open PNG/JPEG/WebP/BMP, direct PNG export.
- ⬜ Export dialog (PNG/JPEG/TIFF/WebP/GIF + quality/compression/resize/preview) · export persona/slices/presets · PSD import/export · TIFF/EXR/HDR/RAW import · SVG/PDF/EPS export · place/embed · batch · metadata/EXIF.

### 16.13 History / non-destructive
- ✅ linear undo/redo, `.sable` keeps params.
- ⬜ History panel · non-linear history/states · named snapshots · history-brush source · configurable depth/disk-backed.

### 16.14 Workflow / UI
- ✅ dark chrome, grouped toolbar+flyouts+hotkey cycle, tool-specific options bar, custom controls, layers/color/gradient panels.
- ⬜ `Dock.Avalonia` docking (float/tab/auto-hide) · saved workspaces · command palette · customisable shortcuts + PS/Affinity presets · right-click context menus · status-bar live info (zoom/coords/colour) · autosave/crash recovery · macros/actions+batch · plugin API · preferences dialog · real Brushes/Swatches/Channels/Paths/Navigator/History/Character/Paragraph panels (several placeholders) · multi-monitor float.

### 16.15 AI (M3, parked)
- ⬜ SAM2 smart select · background removal · upscale (Real-ESRGAN) · object removal (LaMa) · generative fill/expand (Diffusers sidecar) · model manager + VRAM gating.

### 16.16 Advanced / pro — OUT OF SCOPE (user decision, deferred indefinitely)
- ⛔ HDR merge/tone-map · panorama stitch · focus stacking · frequency separation · liquify · lens correction/defringe · 360/equirectangular · astro stacking · channels editing · apply-image · displacement maps · pattern generation. (Everything else in §16 is in scope.)

### 16.17 Highest-impact missing (suggested priority)
1. ✅ Full **blend-mode set** (done) + ⬜ fill opacity + ⬜ Blend-If.
2. **Curves** + a few more adjustments (Vibrance/Exposure/Colour Balance/Gradient Map/Invert/B&W).
3. **Layer FX** (drop shadow/stroke/glow) — high visual value.
4. **Multi-document tabs** + **Export dialog** (workflow basics).
5. Brush **hardness/flow UI** + HUD adjust + presets; **pencil**.
6. **Guides/grid/snapping/rulers** + align/distribute.
7. **Healing/spot/patch** + content-aware fill (some need AI).
8. **History panel** + merge/flatten/duplicate/rasterise/lock ops.
9. **Pen + node editing** (true vector) + shape **stroke**.
10. **Swatches** + alpha + colour models; **16-bit/ICC**.

Sources: [Affinity tools (Edits101)](https://edits101.com/affinity-photo-tools-a-complete-guide/) · [Affinity Wiki](https://affinity.fandom.com/wiki/Affinity_Photo) · [PS layer masks](https://helpx.adobe.com/photoshop/using/editing-layer-masks.html) · [PS smart objects](https://helpx.adobe.com/photoshop/desktop/create-manage-layers/smart-objects/smart-objects-overview-and-benefits.html) · [PS blend modes](https://helpx.adobe.com/photoshop/using/layer-opacity-blending.html) · [PS select & mask](https://helpx.adobe.com/photoshop/desktop/make-selections/refine-modify-selections/refine-your-selection-and-mask.html) · [Raster editors (Wikipedia)](https://en.wikipedia.org/wiki/Raster_graphics_editor).

---

## 17. Cross-cutting / infra / UX backlog (all in scope)

Not in the §16 feature comparison but needed. ⬜ all not started.

### 17.1 App lifecycle / infra
- ✅ **Settings store + Settings dialog (DONE, Phase 2 #3)**: `Sable.Core.Settings.SableSettings` + `SettingsService` (JSON at `%AppData%/Sable/settings.json`, pure/tested). **Affinity-style `SettingsWindow`** — search + category sidebar (General · User Interface · Performance · Colour · Machine Learning · Updates · About) + grouped right pane with **pill `ToggleSwitch`es** (bespoke ControlTheme), sliders, dropdowns. Settings: reopen-on-startup, limit-initial-zoom, save-thumbnails, default DPI, theme, UI density, tooltips, undo limit, view quality, file-recovery interval, renderer (read-only), dither gradients, auto-update, version/about. Wired now: reopen-on-startup (gates restore), default DPI (New dialog), theme; the rest are stored for their features to consume.
- ✅ **Window/session restore + recent files (DONE)**: window size/pos/maximized saved on close + applied on launch; `OpenTabs` (saved-file paths) reopened on `Opened`; `RecentFiles` (deduped, capped 12) → **File▸Open Recent** submenu, recorded on every open/save.
- ✅ **Theming engine (DONE)**: `Theme.axaml` `ThemeDictionaries` (Dark / Gray / Light) define `Chrome*` brush tokens; Gray = custom `Themes.Gray` `ThemeVariant` (inherits Dark). `MainWindow.ApplyTheme` sets `RequestedThemeVariant` from the setting; **MainWindow chrome surfaces bound via `{DynamicResource Chrome…}`** so Dark/Gray/Light re-theme live. 🔶 remaining: the **dialog windows + many inline text colours aren't tokenised yet** (Light theme has text-readability gaps until text tokens are added) — finish by tokenising the rest opportunistically.
- ✅ **Reusable controls (DONE)**: `src/Sable.App/Controls/` — `LabeledSlider`, `HexColorField`, `SettingRow`; new panels compose these instead of re-rolling Grid+Slider+TextBox (CLAUDE UI conventions). Existing dialogs migrate opportunistically.
- ⬜ **Customisable canvas-overlay appearance (settings)**: every hardcoded overlay visual must become a user setting — selection marching-ants **colour + line width + dash speed**, **mask / quick-mask overlay colour + opacity** (rubylith), **guide colour**, **smart-guide colour**, **grid colour + spacing + subdivisions**, **pixel-grid colour**, **pasteboard colour** (currently theme-derived), **ruler unit** (px/mm/in/%), brush-cursor ring style. Store in `SableSettings`, edit via a new `SettingsWindow` "Canvas / Appearance" category (compose `HexColorField`/`LabeledSlider`), and feed them into the blit uniform (`fullscreen_blit.wgsl` consts → uniform fields), `Ruler.cs`, and the selection render — these are hardcoded constants today.
- ⬜ **Rebindable hotkeys**: a keymap model (`action → gesture`, persisted in `SableSettings`) + a **Keyboard-Shortcuts settings page** (searchable action list, live conflict detection, PS/Affinity presets, per-action reset). Route every shortcut through it — the hardcoded `MainWindow.OnGlobalKeyDown` switch, `CycleGroup` tool letters, and menu `InputGesture`s — instead of literal `Key.*` cases. (All shortcuts are hardcoded today.)
- **File associations**: double-click `.sable` opens app, "Open with", OS file-type registration via installer.
- **Telemetry / crash reporting** (opt-in) + **logging/diagnostics** framework.
- **Autosave / crash recovery** (periodic snapshot of open docs; recover on next launch).

### 17.2 Editing / UX gaps
- **Smart guides** (alignment hints while moving) + **snap to layer edges/centres** (distinct from static guides/grid in §16.1).
- **Zoom UI** — fit / 100% / zoom-% field in the status bar (only keys/wheel today).
- **Layer rotate/flip** quick ops (90° CW/CCW, free, flip H/V) — separate from the transform gizmo.
- **Eyedropper options** — sample size (point/3×3/5×5), sample active-layer vs all-layers; sample anywhere on screen.
- **Non-destructive crop** toggle ("keep cropped pixels") + crop **ratio presets**.
- **Quick export** / export-selection-only / export-each-layer.
- **Tab UX** — reorder tabs, overflow scroll when many, detach tab to its own window.

### 17.3 Architecture debt (real, affects scale)
- **Layers are NOT actually GPU-tiled**: `PixelLayer` is one doc-sized buffer; the 256² tiling exists only for undo snapshots + partial upload. The §3 "tiled, GPU-resident" invariant isn't truly met → VRAM/perf blows up on 100MP docs and with many open tabs. Real tiled layer storage (atlas, partial residency, eviction) is the big engine refactor for scale. Tie to the composite-cache perf work.
- **HiDPI / per-monitor DPI**: canvas assumes 1:1; needs render-scaling-aware surface sizing + input mapping.
- **Doc-swap GPU buffer leak** (already noted §15) — must fix before multi-tab (each tab swap currently leaks).

### 17.4 Maybe / TBD
- **Localization / i18n** (CLAUDE.md references locale JSON — decide if Sable ships localizable strings).
- Accessibility (keyboard nav, screen-reader labels).

---

## 18. Implementation roadmap (consolidated — canonical build order)

Sequences everything in §14/§15/§16/§17 into dependency-ordered phases. The old §8 M0–M5 are superseded for remaining work by this. **Status: §16/§17 baseline = M0–M2 done, mid-M4.** Two tracks can run in parallel: **Engine** (compositor/effects/selection) and **App** (chrome/workflow/IO) — noted per phase. Each item = its own ticket; ship + test each.

### Phase 0 — Prereqs / debt to clear first  ✅ DONE
- ✅ **Doc-swap GPU buffer leak fixed**: `GpuCompositor.ReleaseLayerCaches()` (releases `_layerBuffers` + `_maskBuffers`); `GpuSurfaceControl.Document` setter calls it on swap (guarded by ref-equality). No leak when opening/switching docs → unblocks multi-tab.
- ✅ **Full blend-mode set** (not just groundwork — done): `BlendMode` enum now 30 (PS set + Affinity extras), `composite.wgsl` `blend()` implements all incl. non-separable Hue/Sat/Colour/Luminosity + Darker/LighterColour (W3C `setLum`/`setSat`/`clipColor` helpers) and per-channel ColorBurn/Dodge/SoftLight/HardLight/VividLight/PinLight/LinearLight/HardMix/Difference/Exclusion/Subtract/Divide/Average/Negation/Reflect/Glow. Layer-panel blend ComboBox auto-lists all (binds `BlendModes` = `Enum.GetValues`). WGSL validated (compiles at pipeline creation = startup OK). **Phase 1 #1 remainder: fill-opacity + Blend-If still ⬜.** (Polish: prettify enum display names "ColorBurn"→"Colour Burn".)
- ✅ **THIRD_PARTY_NOTICES.md** stub at repo root (libs + licences + watch-list); CI licence-audit step still ⬜.

### Phase 1 — "Feels complete" editor core  *(Engine track, cheap × high-impact, §16.17 top)*
1. ✅ Full blend-mode set (~30) · ✅ fill opacity (`Layer.FillOpacity` → composite.wgsl `params.fillOpacity`, Fill slider, serialized) · ⬜ **Blend-If** (next: expand Params to blend-range — This-Layer + Underlying black/white, luminance factor multiplies `sa`) — `composite.wgsl` switch + per-layer params + UI (§16.3).
2. **Adjustments expansion** — ✅ Curves · Exposure · Vibrance · Threshold · Posterise · Invert · B&W · White Balance · Colour Balance · Channel Mixer · Levels output-levels (GPU + toolbox + serialize; adj buffer now 64B/12-param). ✅ Shadows/Highlights. ⬜ LUM-channel curve · Gradient Map (reuse curve LUT path) · Selective Colour (deferred). Each = `adjust.wgsl` case + PackParams + toolbox + serializer (recipe exists).
3. ✅ **Layer FX** (per-layer non-destructive stack) — Drop Shadow, Outer Glow, Stroke, Colour Overlay, Inner Shadow, Inner Glow, Gradient Overlay, **Bevel/Emboss** (8 effects); compositor FX pass honours Effects-list order; EffectsWindow master-detail + per-FX blend UI + Move Up/Down reorder + serialize (§16.6). 3D out-of-scope; Pattern/Long-Shadow + full drag-reorder = later.
4. ✅ **More live filters** — Box/Motion/Zoom blur, Sharpen/Unsharp/High-Pass/Clarity, Add Noise/Denoise (10 total) + **filter mask/opacity closed** (filter→scratch then `BlendBufferInto` with layer opacity+mask). Toolbox Radius/Amount/Angle per kind, menu + flyout, serialized, GPU smoke. (§16.5)
5. ✅ **Clipboard** — Copy/Cut/Paste/Copy-Merged/Paste-Into/Duplicate(Ctrl+J), layer + selection region, **internal `SableClipboard`** + `Layer.Clone()` + **OS clipboard image** (Avalonia 12 `ClipboardExtensions.Set/TryGetBitmapAsync`) (§16.2).
6. ✅ **Layer ops** — duplicate, merge-down/flatten/merge-visible/stamp, rasterise (GPU-collapse + `ReplaceLayersCommand`), locks (pos/pixels/alpha), colour tags, between-row drop-reorder + indicator (§16.3).
7. ✅ **Brush** — hardness + flow UI, **HUD adjust** (Ctrl+Alt-drag), pencil, eyedropper options (sample size + all-layers). ⬜ gradient radial/conical/reflected = deferred (gradient flagged incorrect) (§16.7, §17.2).

### Phase 2 — Document & workflow infrastructure  *(App track, can parallel Phase 1)*
1. ✅ **Multi-document tabs** — tab strip, per-tab Document+VM, switch/close(unsaved prompt), no-demo start/welcome, New dialog, New from clipboard, drag-drop files → new tab (§15 backlog).
2. ✅ **Export dialog** — PNG/JPEG/WebP + quality + resize + preview/est-size (`ExportDialog`, `ImageCodec.EncodeScaled`, `DocumentIO.Export`). ⬜ TIFF/GIF (Skia can't encode TIFF) (§16.12).
3. ✅ **Settings store + Preferences dialog + window/session restore + recent files** (`SableSettings`/`SettingsService`, `PreferencesWindow`, Open-Recent menu). 🔶 light/gray **chrome** theming still TODO (setting wired to Fluent variant; inline chrome colours need tokenising) (§17.1).
4. ✅ **About dialog** (Help▸About: version/licence/runtime + manual check) + **UpdateService** (`Sable.Core.Services`, GitHub-releases check + per-OS asset + download + LaunchInstaller; `UpdateWindow` does download-progress→install→shutdown; launch check honours `AutoCheckUpdates`; **points at `Drommedhar/novalist-official` for testing until `sable` is public** — TODO in `UpdateService`) + **CI/CD** (`.github/workflows/ci.yml` build+test; `release.yml` tag→ Win installer (Inno) + macOS DMG + Linux AppImage + GitHub release; `.github/installers/{windows/sable.iss,macos/Info.plist.template}`). `VersionInfo` from assembly; `VersionPrefix` in Directory.Build.props (0.1.0). (§15 backlog).
5. ✅ **Rulers / guides / grid / snapping + smart guides + zoom UI + status-bar live info** (§16.1, §17.2). **Zoom UI** (status-bar Fit/1:1/zoom-% box + View▸Zoom In/Out/Fit/Actual; canvas `EffectiveScale`/`SetZoomPercent`/`ViewChanged`). **Live status bar** (zoom %, doc `W×H @ dpi`, cursor doc coords via `CursorDocMoved`). **Grid** (GPU `fullscreen_blit.wgsl` doc-grid + 1px pixel-grid; View toggles). **Rulers** (`Controls/Ruler.cs` top/left, nice-stepped ticks + labels + cursor marker + guide markers, themed via `ActualThemeVariant`, fed `Canvas.ViewportDip`; View▸Show Rulers). **Guides** (`Document.GuidesX/Y`, drawn via a storage buffer bound into the blit shader, click a ruler to create, drag on canvas to move / drop-outside to delete, serialized in `.sable`). **Snapping** (`Canvas.SnapAxis` → move-offset + marquee snap to guides/grid/doc-edges; View▸Snap; Clear Guides). **Smart guides** (`SmartSnap`: moved layer's L/centre/R + T/centre/B snap to other layers' edges/centres + doc edges/centre; magenta alignment lines via a 2nd blit storage buffer, shown during the drag only).
6. ✅ **File associations** (Windows `.sable` registry assoc in `sable.iss`; macOS `CFBundleDocumentTypes` in `Info.plist.template`; Linux `.desktop` `MimeType`; `App` passes `desktop.Args` → `MainWindow.OpenLaunchArgs`/`OpenPath`) + **autosave/crash-recovery** (`RecoveryService`: per-tab `.sable` to `%AppData%/Sable/Recovery` + manifest on a `DispatcherTimer`; clean exit clears it, so leftovers on launch ⇒ unclean prev run ⇒ `OfferCrashRecovery` prompt restores tabs; `SableSettings.AutosaveEnabled`/`AutosaveMinutes`) (§17.1).

### Phase 3 — Selection & masking depth  *(Engine track)*
- 🔶 Polygonal + magnetic lasso, selection brush, colour-range select, grow/shrink/smooth/border, **refine-edge/select-and-mask**, **quick mask (Q)**, invert, save/load selection (+ serialize), transform selection, feathered fill/delete (§16.2). ✅ **Select menu** (All/Deselect/Invert + **Grow/Shrink/Smooth/Border/Feather** = `Selections.Full/Invert/Grow/Shrink/Smooth/Border` morphology, Ctrl+A/D/Shift+I). ✅ **Polygonal lasso** (`ToolKind.PolyLasso`, L-group cycle; click vertices, click-first-vertex or Enter to close, Esc cancel; `_polyPts`/`CommitPolyLasso`/`CancelPolyLasso`). ✅ **Colour-range select** (`Selections.ColorRange` on the doc-sized composite; `ToolKind.ColorRange`, W-group cycle). ✅ **Save/Load selection** (`Document.SavedSelection`, Select menu, `.sable` `selection.raw` entry). ✅ **Feathered delete** (`DeleteSelection` erases alpha ∝ mask coverage, not binary). ✅ **Quick mask (Q)** + **selection brush** (`Canvas.ToggleQuickMask`: editable RGBA8 `_qmask`, brush paints it — white adds, eraser/black removes — synced to `Document.SetSelectionMaskLive` each segment + on undo; rendered as translucent-red rubylith via blit `maskOn==2`; verified). ✅ **Transform selection (move)** (`Selections.Shift`; Marquee drag-interior on a mask selection translates it live, normalised on release). ⬜ transform-selection **scale** gizmo; **magnetic lasso** + **refine-edge/select-and-mask** deferred (edge-detect / hair-matte, AI-adjacent — revisit with Phase 8 AI).

### Phase 4 — Vector & text depth
- **Pen tool + bézier node editing**, shape **stroke/fill/dash/joins** + boolean ops + more shapes (polygon/star/arrow/custom), **text-on-path**, frame/area text + wrap, character/paragraph panels (kerning/tracking/super-sub/lists), text styles, text→curves (§16.10).

### Phase 5 — Retouch & transform depth
- Healing brush / spot heal / patch, perspective clone; non-uniform scale + edge handles, skew/shear, perspective/distort, warp/mesh warp, content-aware scale, liquify, align/distribute, numeric transform panel, layer rotate/flip quick ops (§16.8, §16.9, §17.2). (Content-aware fill → needs Phase 8 AI.)

### Phase 6 — Colour & IO depth
- Swatches/palettes panel, alpha in picker, RGB/HSL/CMYK/LAB sliders, fg/bg swatches, gradient/pattern presets, gamut/soft-proof, histogram/info panels; **16/32-bit + ICC pipeline** (UI + convert at boundaries); **PSD import/export**, TIFF/EXR/HDR/RAW import, SVG/PDF export, place/embed, batch (§16.11, §16.12).

### Phase 7 — Architecture & scale  *(Engine track, heavy)*
- **Real GPU-tiled layer storage** (atlas + partial residency + eviction) — meet the §3 invariant; unblocks 100MP + many tabs.
- **Composite-cache** (cache backdrop below active layer; region recompositing) for big-doc paint.
- **HiDPI / per-monitor DPI**.
- **History panel** + non-linear history/states + named snapshots.
- **Dock.Avalonia docking** + saved workspaces + command palette + **rebindable hotkeys (keymap + shortcuts settings page)** + **customisable canvas-overlay appearance settings** (selection/mask/guide/grid colours + sizes — see §17.1) + macros/actions + plugin API; real Brushes/Channels/Paths/Navigator panels (§16.13, §16.14).

### Phase 8 — AI (was M3)
- ONNX light tier in-process: SAM2 smart-select, BiRefNet/RMBG bg-removal, Real-ESRGAN upscale, LaMa object-removal. Then Diffusers **sidecar** (uv venv, IPC) for generative fill/expand + **model manager + VRAM gating** (§6, §16.15).

### Phase 9 — Cross-platform
- Real **Linux** (Xlib/Wayland) + **macOS** (CAMetalLayer) backends (surface + input) — seam ready (`IPlatformBackend`/`IInputSource`). Per-OS packaging.

### Phase 10 — Polish & release (was M5)
- Tablet pressure/tilt, telemetry/crash (opt-in), perf pass (brush latency, large docs), packaging/signing/**notarisation** (MSIX/MSI · AppImage/Flatpak · .app), docs, i18n decision, accessibility.

**Sequencing notes**: Phases 1 & 2 are the big near-term value and run in parallel (Engine vs App). Phase 0 leak fix gates Phase 2 tabs. Phase 7 tiling is the one large engine refactor — schedule before heavy multi-tab/100MP use bites. Out of scope: §16.16 (advanced/pro).
