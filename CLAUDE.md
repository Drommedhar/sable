# Sable — project guide for Claude

Sable = cross-platform (Windows/Linux/macOS) **raster** image editor, Photoshop/Affinity look-and-feel, GPU-first, with local AI (selection, repair, upscale, generative fill).

**Full design doc: [PLAN.md](PLAN.md). Read it before any architectural work.**

## Locked decisions (see PLAN.md §1)
- **UI**: AvaloniaUI for chrome only. Canvas = separate GPU surface embedded via `NativeControlHost`. Never render the canvas with Avalonia/Skia.
- **GPU**: GPU-first, **wgpu** (one API → DX12/Vulkan/Metal). Spike uses `Silk.NET.WebGPU` binding.
- **AI**: light tier (SAM2/BiRefNet/ESRGAN/LaMa) = in-process ONNX Runtime, ships with app, no Python. Generative tier (SD/Flux/Qwen via **Diffusers**, Apache-2.0) = opt-in install, `uv` venv sidecar over IPC. **No ComfyUI.**
- **AI compute**: GPU-only, no CPU fallback. Block op if VRAM won't fit.
- **Models**: user-provided weights only, no bundled catalog.
- **License**: MIT. Keep GPL out. ImageSharp split-license = watch.
- **Effects are layers**: adjustments + live filters are first-class tree nodes (Affinity model), also attachable as per-layer FX. EVERYTHING non-destructive + undoable — enforced by the graph/compositor, not per-feature. New effect MUST be a graph node (serializable params + WGSL pass + undo entry).
- **Name**: Sable. Native format `.sable`. Repo folder still `CrossDraw/` — rename later.

## Architecture invariants (do not break)
- Document = graph, not pixel buffer. On-screen image is always a recompute by the GPU compositor.
- Tiled layer storage (256×256), GPU-resident, dirty-tile undo snapshots.
- Working space: linear float (RGBA16F/32F); ICC convert at boundaries.
- Engine (`Sable.Engine`) is UI-agnostic + headless-testable. MVVM (CommunityToolkit.Mvvm) for UI↔engine.

## Module layout (PLAN.md §7)
`Sable.App` (Avalonia shell) · `Sable.UI` · `Sable.Canvas` (GPU host) · `Sable.Engine` (doc/layer/compositor graph) · `Sable.Gpu` (wgpu binding + WGSL) · `Sable.Imaging` (codecs/color/tiling/IO) · `Sable.Tools` · `Sable.Ai` (ONNX) · `Sable.Ai.Sidecar` (Diffusers) · `Sable.Format` (.sable IO/history) · `Sable.Core` (math/color/undo/settings).

## Current state
- Solution scaffolded: `Sable.slnx` + 11 `Sable.*` projects under `src/` + `Sable.Gpu.Spike` console. TFM **net10.0** (SDK 10), Avalonia 12.0.4, Silk.NET.WebGPU 2.23.0.
- **M0 spike #1 PASS**: `Sable.Gpu.Spike` composites 2 RGBA8 layers with a WGSL Normal-blend compute pass on the GPU, reads back, writes `spike_out.png`. Verified on AMD Radeon PRO / Vulkan. Proves wgpu binding + compute + readback.
- Avalonia shell (`Sable.App`) runs: dark PS/Affinity layout skeleton (menu/options bar/tool strip/Color+Layers panels) per §13. Static — no docking lib yet.
- **M0 spike #2 PASS**: `Sable.Canvas/GpuSurfaceControl.cs` embeds a live wgpu swapchain in Avalonia via `NativeControlHost` (Win32 HWND → `InstanceCreateSurface` → configure → present loop on a 16ms `DispatcherTimer`). **Clears the #1 architectural risk (GPU canvas composited into Avalonia, not Skia).** Windows-only so far.
- **M0 spike #3 PASS**: `Sable.Gpu/BlendDemoRenderer.cs` runs the blend compute → rgba8 texture (`CopyBufferToTexture`) → fullscreen-triangle blit (`fullscreen_blit.wgsl`) → swapchain. Real composited pixels render live inside the Avalonia canvas (verified visually). Full GPU loop closed: compute → texture → render → present → embedded.
- **M1 engine — first slice DONE**: real document model + N-layer GPU compositor driving the embedded canvas.
  - `Sable.Core`: `BlendMode` (Normal/Multiply/Screen/Overlay/Darken/Lighten/Add — int values are the WGSL contract), `Undo/UndoStack` + `IUndoableCommand`.
  - `Sable.Engine`: `Document` (bottom→top `Layers`, `CreateDemo()`), `Layers/Layer` (abstract: opacity/blend/visible/dirty), `PixelLayer` (full-res RGBA8). `Compositing/GpuCompositor` walks layers, blends via `composite.wgsl` ping-ponging two storage buffers, copies result→texture. Recomposites only when `doc.AnyDirty`.
  - `Sable.Gpu`: `SurfaceBlitter` (reusable fullscreen blit), `WgpuDevice.CreateWgslModule`, `Shaders/composite.wgsl`.
  - `GpuSurfaceControl` now renders a `Document` via compositor+blitter. Verified: 3-layer demo (gradient + red disc + Screen highlight) composites correctly in-window.
- **M1 layers panel bound (DONE)**: `Sable.UI/ViewModels/{DocumentViewModel,LayerViewModel}` (CommunityToolkit.Mvvm). MainWindow shares one `Document` between `GpuSurfaceControl` and the VM. Layer rows (ListBox), visibility checkbox, blend ComboBox, opacity slider are two-way bound; setters flag `layer.Dirty` → canvas recomposites next tick. Verified: panel shows real layers (Highlight/Red Disc/Background) with correct blend/opacity.
- **M1 PNG import/export (DONE)**: `Sable.Imaging/ImageCodec` (SkiaSharp decode/encode RGBA8), `Sable.Engine/IO/DocumentIO` (OpenImage→Document, ExportPng). `GpuCompositor.CompositeToBytes` flattens on GPU + reads back. File menu: Open Image / Export PNG via Avalonia StorageProvider. **Present step now uses a `present_copy.wgsl` compute `textureStore`** (not CopyBufferToTexture) so document width is unconstrained — verified at 643px (non-256-aligned) export.
- **M1 layer ops + undo (DONE)**: `Sable.Engine/Commands/LayerCommands` (Add/Remove/Move, all `IUndoableCommand`). `DocumentViewModel` runs them through its `UndoStack`, resyncs the layer list on `Undo.Changed`, exposes `[RelayCommand]` NewLayer/DeleteLayer/MoveLayerUp/MoveLayerDown/UndoEdit/RedoEdit. Footer buttons (＋🗑▲▼↶↷) + Edit▸Undo/Redo (Ctrl+Z/Y) bound. `Document.NeedsComposite`/`MarkStructureChanged` drive recomposite on structural change. Undo/redo logic verified headlessly (add→move→undo→undo→redo exact).
- **M1 viewport (DONE)**: `Sable.Gpu/ViewportTransform.Fit` (aspect-fit + zoom + pan, pure/tested: 512² in 1000×800 → ox=100,scale=1.5625). `fullscreen_blit.wgsl` maps surface pixels → doc UV, checkerboard outside doc, composites layer alpha over checker. `GpuSurfaceControl` holds zoom/pan + `ZoomBy/PanBy/ResetView`. Input: Window keyboard (+/-/0, arrows) guaranteed; transparent overlay handles mouse wheel/drag best-effort (**native-HWND airspace may swallow mouse over the canvas** — keyboard is the reliable path; revisit with a proper input strategy).
- **M1 brush tool (DONE)**: `Sable.Tools/BrushTool` (soft round dab, src-over into PixelLayer RGBA8, stroke interpolation). **Airspace solved**: `GpuSurfaceControl.Input.cs` subclasses the native child HWND's WndProc (`SetWindowLongPtrW`/`GWLP_WNDPROC`), captures WM_LBUTTON*/MOUSEMOVE, maps surface px → doc px via inverse `ViewportTransform`, drives the brush, marks layer dirty → live recomposite. `ActiveLayer` set from the selected layer. **Verified live** (synthetic WndProc strokes painted white onto the canvas through the full path).
- **M1 tiling + paint undo (DONE)**: `PixelLayer` has 256² tile accessors (`GetTile`/`SetTile`, edge-aware). `Sable.Tools/StrokeSession` snapshots touched tiles copy-on-first-touch across a gesture; `PaintLayerCommand` holds before/after tile bytes (bounded memory). `GpuSurfaceControl.CommandProduced` → routed to the VM `UndoStack` in MainWindow, so brush strokes are undoable on the same stack as layer ops. `Ctrl+Z`/`Ctrl+Y`/`Ctrl+Shift+Z` KeyBindings added (Avalonia MenuItem InputGesture is display-only). **Verified live**: paint stroke → Ctrl+Z reverts it. Headless: painted=25207 → undo=0 → redo=25207.
- **M1 `.sable` save/load (DONE)**: `Sable.Format/SableFile` — zip container, `document.json` (size + layer params) + `layers/{i}.raw` (RGBA8, deflate). `ZipArchive` + `System.Text.Json`, no deps. File menu: Open (Ctrl+O) / Open Image / Save (Ctrl+S) / Save As / Export PNG; `_currentPath` tracks Save vs Save As. Verified round-trip: size/layer-count/blend/opacity/pixels preserved.
- **M1 adjustment layer (DONE)** — first effects-are-layers node (PLAN §5): `Sable.Engine/Layers/AdjustmentLayer` (Brightness/Contrast, no pixels). Compositor walks it like any layer and runs `adjust_bc.wgsl` over the accumulated backdrop below (opacity = strength); `DispatchAdjust` + `_adjPipeline`. VM: `LayerViewModel.IsAdjustment`/`Brightness`/`Contrast`, `DocumentViewModel.NewAdjustmentCommand`; UI footer ◑ button + Brightness/Contrast sliders shown when an adjustment is selected. Serialized in `.sable` (Type="adjustment"). Verified: visible contrast boost, 4-layer demo, round-trips. **New adjustment = AdjustmentKind + WGSL pass + the compositor branch + serializer case.**
- **M1 per-layer masks (DONE)**: `Layer.Mask` (RGBA8, R channel = coverage; `AddWhiteMask`/`RemoveMask`/`HasMask`/`MaskDirty`). Compositor binds a mask buffer to both blend + adjust passes (composite.wgsl binding 5, adjust_bc.wgsl binding 4); pixel alpha *= mask, adjustment strength *= mask. Layers without a mask use a shared full-size white buffer (`_whiteMask`). Mask buffers cached (`_maskBuffers`) + serialized in `.sable` (`masks/{i}.raw`). Verified: demo disc masked by a vertical gradient fades top→bottom; round-trips.
- **M1 mask painting + viewport fixes (DONE)**: BrushTool/StrokeSession/`PaintRasterCommand` generalized to paint any RGBA8 `(byte[], w, h)` target (tile ops moved to `RasterTiles`); brush paints layer pixels OR mask. `GpuSurfaceControl.PaintMask` toggle (key **M**) — auto-adds a white mask, paints black = hide; undoable on the same stack. **Pan + zoom now in the WndProc subclass** (reliable native path): middle-drag = pan, wheel = zoom; **zoom anchors to the cursor** (`ZoomAt`); blitter sampler **MagFilter=Nearest** so zoomed-in pixels are crisp (Linear minify). Keyboard +/-/0/arrows still work. Verified: pan live (image shifts); headless brush/undo on byte[] (1184px, 25207→0→25207). Mask-paint + zoom-to-cursor logic in but not screenshot-verified (busy desktop blocks reliable window capture — user verifies live).
- **M1 brush color/size UI (DONE)**: Avalonia `ColorView` (HSV picker, `Avalonia.Controls.ColorPicker` pkg + Fluent theme include) in the Color panel → sets `Canvas.Brush.R/G/B`; brush-size slider in the options bar → `Brush.Radius` (slider = diameter). Code-behind handlers (`OnBrushColorChanged`/`OnBrushSizeChanged`), swatch preview. Brush color now drives both pixel paint and mask paint (black on mask = hide, white = reveal) — removed the hardcoded color overrides. Build clean (ColorView theme loads, no crash); not screenshot-verified (desktop blocks capture — user verifies live).
- **M1 eraser + eyedropper (DONE)**: `BrushTool.Erase` (destination-out: reduces alpha, keeps color); `GpuSurfaceControl.EraseMode` toggle (key **E**), undoable like paint. Eyedropper = **Alt+click** (`GetKeyState` VK_MENU) samples `ActiveLayer.Pixels` → sets brush color + raises `ColorPicked` → updates the ColorView + swatch. Verified headless: erase 1968→844 px (center fully cleared, soft edges partial).
- **M1 clip + adjustments + fill + partial-upload (DONE, all unit-tested — 37 tests)**:
  - **Clip-to-layer**: `Layer.ClipToBelow`; composite.wgsl `params.clip` multiplies coverage by backdrop alpha; UI checkbox; serialized.
  - **Adjustments unified**: `adjust.wgsl` switches on `kind` (BrightnessContrast/Levels/HSL) with generic p0..p5; `AdjustmentLayer.PackParams`; Image▸Add Adjustment menu; serialized. **Recipe: new adjustment = AdjustmentKind + a case in adjust.wgsl + PackParams + toolbox sliders + serializer fields.**
  - **Adjustment params = modeless toolbox** (`AdjustmentWindow.axaml`, NOT in the layer panel): floating window bound to the same `DocumentViewModel`, shows per-kind sliders for the selected adjustment layer. Auto-shows when an adjustment layer is selected; toggle via **Window ▸ Adjustments**; closing hides (not destroys). Layer-row representation of filters is deliberately unchanged (will be redone later).
  - **Fill/bucket**: `Sable.Tools/FillTool.Flood` (scanline 4-connected, tolerance), key **F**, click-to-fill, undoable (whole-layer tile snapshot).
  - **Partial GPU upload**: `Layer.DirtyTiles` (set) + `MarkTilesDirty`; brush/fill/PaintRasterCommand report touched tiles via `Action<IReadOnlyCollection<(int,int)>>`; `GpuCompositor.GetLayerBuffer` uploads only dirty tiles row-by-row (new buffer / no-tile-info → full upload). Mask still full-upload.
- **M1 live filter — Gaussian blur (DONE)**: `Sable.Engine/Layers/FilterLayer` (FilterKind, Radius) — non-pixel node, blurs the backdrop below it. `blur.wgsl` separable Gaussian (premultiplied), 2 dispatches H+V via `_filterTemp`; compositor `FilterLayer` branch + `DispatchBlur`. Filter menu ▸ Gaussian Blur; toolbox Radius slider (shown for `IsFilter`); serialized. Verified: 38 tests + GPU smoke (5-layer composite visibly blurred). **Recipe: new live filter = FilterKind + WGSL pass(es) + compositor branch + toolbox sliders + serializer.** NOTE: blur ignores mask/opacity for now (applies fully) — mask/opacity-for-filters is a follow-up.
- **Effects toolbox**: `IsEffect` (adjustment OR filter) drives the modeless `AdjustmentWindow`; closing main quits app (`ShutdownMode.OnMainWindowClose`); toolbox is a plain window (no cancel-on-close — that vetoed the owned-parent close).
- **M1 layer groups (DONE)**: `GroupLayer : Layer { List<Layer> Children }` — document is now a tree. Compositor recurses: `CompositeList(list, depth)` composites a group's children into a scratch buffer (pool `_scratch`, ping-pong pair per depth via `ScratchPair`), then `BlendInto` blends the group result with its blend/opacity/mask (**isolated** grouping; pass-through later). `Document.FindParent` + recursive `NeedsComposite`/`ClearDirty`. Parent-aware commands: `AddLayerCommand(doc, parent, layer, index)`, `RemoveLayerCommand`/`MoveLayerCommand(doc, layer, delta)` (operate within parent), `GroupCommand`/`UngroupCommand` (undoable). VM flattens the tree top→bottom with `Depth`/`Indent`/`IsGroup`; footer 🗀 group / ⊟ ungroup buttons. Recursive `.sable` serialize (LayerDto.Children + global entry counter). Verified: 41 tests (group/ungroup/FindParent/nested-serialize) + GPU smoke (6-layer composite incl group). **Deferred: drag-drop into groups (use group/ungroup + move), pass-through groups, multi-select grouping.**
- **M1 multi-select + drag-drop grouping (DONE)**: ListBox `SelectionMode=Multiple` → `DocumentViewModel.SetSelection`; **Group** groups the whole selection (`GroupLayersCommand`, order-preserving). Drag-drop = **manual pointer DnD** (Avalonia 12 reworked the DataObject/DragDrop API, so avoided it): press→move(≥5px)→release; target resolved by `LayerList.InputHitTest(releasePos)` walking the visual tree (NOT `e.Source` — the list captures the pointer so e.Source stays the source row). `DropLayer`: onto a group → `MoveLayerToCommand` into it; onto a sibling → auto-`GroupLayersCommand`; cross-parent → move into target's parent; cycle-guarded. Drag ghost overlay (Canvas in a root Panel) follows the cursor. Verified: 43 tests (multi-group/move-to) + live.
- **Gotcha**: the GPU canvas control has `x:Name="Canvas"`, which shadows `Avalonia.Controls.Canvas` in code-behind — fully-qualify `Avalonia.Controls.Canvas.SetLeft/SetTop`.
- **M1 remaining**: between-row drop reorder + indicator, pass-through groups, more adjustments (Curves/colour balance) + filters (sharpen/unsharp), partial MASK upload, mask/opacity for filters, vector/text. Minor: doc-swap leaks old GPU buffers; HiDPI 1:1; mouse input Windows-only.
- **Controls**: wheel=zoom-to-cursor · middle-drag=pan · 0=fit · +/-=zoom · arrows=pan · left-drag=paint · **M**=mask-edit · **E**=erase · **Alt+click**=eyedropper · Ctrl+Z/Y=undo/redo · Ctrl+O/S=open/save.

## Tools (M2, PLAN §14)
- **Tool framework**: `Sable.Tools/ToolKind` (Move/Marquee/Brush/Eraser/Fill/Eyedropper/Hand/Zoom). `GpuSurfaceControl.ActiveTool` routes WndProc left-button via `OnLeftDown`/`OnLeftUp` switch. Toolbar buttons (`Tag` + `OnSelectTool`, highlight) + key shortcuts (V/M/B/E/G/I/H/Z, K=edit-mask). Options bar shows active tool (`ToolStatus`).
- **Move** (V): non-destructive `Layer.OffsetX/Y`; composite.wgsl samples src+mask at `-offset` (params now 32B: mode/opacity/clip/offX/offY); `MoveOffsetCommand` undoable; offset serialized in `.sable`.
- Existing brush/eraser/fill/eyedropper rerouted through tools. Eraser = Brush.Erase; Alt+click = quick eyedropper in paint tools. Hand=left-drag pan, Zoom=click (Alt=out), wheel=zoom-to-cursor, middle-drag=pan.
- **Pending tools** (PLAN §14.4): Marquee selection (rect/ellipse, GIMP-style in-canvas move/resize grips), Lasso, Magic Wand, Crop, Gradient, Clone, Heal, Dodge/Burn, Shapes, Type, Pen. Flyout grouping + per-tool options bar to follow.
- **Selection combine modes (DONE)**: Shift=add, Alt=subtract, Shift+Alt=intersect, none=replace — rect/ellipse/lasso/wand. `Selections.Combine`/`Rect` + `Document.SnapshotSelectionMask`; gesture wiring `CaptureSelMode`/`ApplyMask` in `GpuSurfaceControl.Input.cs`. Plain-rect Replace keeps grips; any modifier rasterizes to a mask.

## Cross-platform (PLAN §2.1/§2.2)
- **Goal: minimize per-OS code — everything shared except the irreducible bits.** Render loop, compositor, viewport, tool logic, coordinate mapping are all platform-agnostic.
- **`Sable.Canvas/Platform/IPlatformBackend`** is the one seam: `CreateSurface(gpu, handle)` (native window → wgpu surface), `CreateInput()` (native event source), `RaiseTimerResolution()`. `CanvasPlatform.Current` picks `WindowsBackend` (real) or `UnsupportedBackend` (Linux/macOS stub). `InitGpu` catches `PlatformNotSupportedException` → blank canvas, no crash; chrome still runs. **Adding an OS = one new backend.**
- **Input is platform-agnostic** (verified): `IInputSource` decodes native events → shared `ICanvasInputSink` (`PointerDown/Move/Up/Wheel`, surface coords + `CanvasMods`). `WindowsInputSource` = the WndProc subclass (decode only); all tool logic lives in `GpuSurfaceControl`'s sink impl. **No Win32 P/Invoke remains in `GpuSurfaceControl`** — only in `Platform/Windows*`. A new OS needs only a new `IInputSource` + surface descriptor; wgpu itself is already cross-platform.

## Brush preview (in-stack)
- Live preview = a **stamp dab composited into a COPY of the active layer's buffer** (`_previewBuf`) *before* that layer blends — so erase reveals layers below and paint respects the layer's blend/opacity. NOT drawn on top in the blit (that broke erase → showed transparent). Cursor *ring* is the only blit-overlay part. `GpuCompositor.Preview` (`PreviewDab`); control sets it each frame for Brush/Eraser at the doc cursor and forces a recomposite while hovering; cleared during a stroke and before export.
- Layer GPU buffers need `CopySrc` (for the preview copy) — `GetLayerBuffer` allocates `Storage|CopyDst|CopySrc`.

## Gotchas (learned)
- **Avalonia 12 compiled bindings need `x:DataType`** on the binding scope (Window root + each `DataTemplate`), else `AVLN2100`. 
- **`dotnet build` can report success while XAML is broken** (incremental up-to-date skip leaves stale/no precompiled XAML → runtime `XamlLoadException`). After XAML edits, do a clean build (`rm -rf obj bin`) or trust the runtime launch, not the incremental "0 errors".
- **Do NOT `dotnet build -o <dir>`** for the Avalonia app — custom output path breaks XAML precompile. Run from `bin/Debug/net10.0/`.
- **Overriding Fluent theme resource keys: match the TYPE, not your guess.** Some keys are not the type they sound like — e.g. `SliderPreContentMargin`/`SliderPostContentMargin` are **`GridLength`**, not `Thickness`. A wrong-type override builds fine but throws `InvalidCastException` at runtime during template apply (startup crash, managed `0xE0434352`). Safe keys used: `SliderHorizontalHeight`/`SliderTrackThemeHeight` (`x:Double`), `SliderTrackFill`/`SliderTrackValueFill`/`SliderThumbBackground` (+PointerOver/Pressed) (`SolidColorBrush`).
- **Verify GUI startup via exit code, NOT a pipe.** `Sable.App.exe 2>&1 | head` returns the pipe's exit (0) even when the app crashes — false "alive". Use PowerShell `Start-Process -PassThru` then check `$p.HasExited`/`$p.ExitCode`. A native fail-fast **0xC0000409** (STATUS_STACK_BUFFER_OVERRUN) faulting in `wgpu_native.dll` is usually a **managed bug**: an out-of-bounds `stackalloc[...]` write (e.g. writing `entries[3]` into `stackalloc X[3]`) corrupts the /GS cookie and unwinds through native code. Check stackalloc sizes match the indices written.
- **wgpu surface goes `Outdated` when occluded/resized** (e.g. a modal file dialog over the window). If `RenderFrame` just `return`s on non-`Success` `SurfaceGetCurrentTextureStatus`, the canvas freezes on stale content until a manual resize forces `Configure`. Fix: on `Outdated`/`Lost`, call `Configure(_width,_height)` and recover next frame. (This was the "image only shows after maximize" bug.)
- **Canvas render `DispatcherTimer` must run at `DispatcherPriority.Render`, not the default Background** — at Background it's starved behind input/layout and the GPU canvas sits at ~30fps with 100ms+ freezes. `timeBeginPeriod(1)` alone does NOT fix it (Avalonia's DispatcherTimer ignores the multimedia timer). Use `new DispatcherTimer(interval, DispatcherPriority.Render, handler)`.
- **Brush/eraser preview = a per-frame dab; only recomposite when it CHANGES.** Recompositing every hover frame full-doc lags large docs. Gate on `NeedsComposite || !Nullable.Equals(dab,_lastPreview)`. (`PreviewDab` is a `readonly record struct` → value equality.)
- **Also pending**: Linux/macOS surface paths, SAM2 ONNX, Diffusers sidecar. Large-doc paint still full-recomposites per brush move (composite-caching is the follow-up).
- F5 in VS Code: `.vscode/launch.json` — default runs `Sable.App`, second config runs `Sable.Gpu.Spike`.

## Build/run
- `dotnet build` (whole solution; finds `Sable.slnx`).
- **`dotnet test`** → `tests/Sable.Tests` (xUnit, 28 tests): Core (BlendMode contract, UndoStack), Engine (Document, layer commands, RasterTiles), Tools (BrushTool paint/erase, StrokeSession paint-undo), ViewportTransform, SableFile round-trip, ImageCodec. Pure-logic only (no GPU). **Add a test here for new pure logic — don't put verification in the spike.**
- Run GPU spike (GPU smoke only: wgpu compute blend + N-layer compositor→PNG): `dotnet run --project src/Sable.Gpu.Spike` → `spike_out.png`, `m1_export.png`.
- Run app shell: `dotnet run --project src/Sable.App`.
- Target: **net10.0** (runtime 10.0.6 present). Shared props in `Directory.Build.props` (TFM, nullable, `AllowUnsafeBlocks`).
- `Sable.Gpu` needs `Silk.NET.WebGPU` + `.Native.WGPU` + `.Extensions.WGPU` (DevicePoll lives in the extension). WGSL shaders embedded from `Sable.Gpu/Shaders/*.wgsl`.

## Conventions
- Caveman comms in chat; normal prose in code/docs/commits.
- New effect = graph node checklist: serializable params + WGSL compute pass + undo entry. Destructive-only effect = bug.

Rules in this file apply to every Claude Code session in this repo. They override generic defaults and persist across conversations.

## When unsure, ASK — always

If a request is ambiguous, has more than one plausible interpretation, or you are about to make a non-trivial design/scope decision: **stop and ask the user before implementing.** Do not guess. Do not pick "the most likely" reading and run with it.

**Why:** guessing wrong on a multi-file feature wastes a full build cycle and the user's time, and it has happened repeatedly. A 10-second clarifying question is always cheaper than a wrong implementation.

**How to apply:**

*   Ask BEFORE writing code, not after. One tight, specific question (or a short numbered list of options) — not "should I proceed?".
*   This applies even under time pressure or when the user seems impatient. A wrong big change is worse than a question.
*   Small, reversible, obvious things (a typo fix, an unambiguous one-liner) don't need a question. Anything touching multiple files, the data model, or UX behaviour does if there's any doubt.
*   If the user already answered a question, don't re-ask it — read carefully.

## No emojis

Do NOT add emoji glyphs anywhere — XAML, locale JSON, C# code (labels / Debug.WriteLine prefixes / log tags), JavaScript, prose responses, menu items, ribbon entries, button content, finding-type markers, or any other surface.

This covers all pictographs in the Unicode emoji blocks:

*   `U+1F300`–`U+1FAFF`
*   `U+2600`–`U+27BF`
*   common offenders: `✒ ✂ 💡 🎨 🎭 🔗 📊 📝 🗑 ➤ ⚠ ➕`

**Acceptable visual markers:**

*   SVG path-geometry strings (Lucide-style, e.g. `M21 15a2 2 0 0 1-2 2H7l-4 4V5...`) used in `IconPath` on ribbon items, activity-bar entries, sidebar contributors, ContentViewDescriptor etc. These are the project's icon system.
*   Non-emoji unicode punctuation when needed and no SVG exists: `× ✕ → ←` for close / arrow buttons.
*   Plain text labels — always preferred.

**Why:** user has stated explicitly that emojis make the app feel like dumb consumer software. This was reinforced by removing every emoji previously introduced (inline actions, context menus, story-analysis filters, chat buttons, finding type icons). Treat this as a hard product-aesthetic constraint, not a stylistic suggestion.

**How to apply:**

*   When adding a new menu item, button, ribbon entry, descriptor, or locale string: use a text label and either an empty `Icon` field or an SVG `IconPath`. Never reach for an emoji as a quick visual marker.
*   When touching a file that already contains emojis (in UI, locales, or labels): strip them as part of the change.
*   Do not put emojis in Debug.WriteLine or console.log prefixes either (e.g. avoid `[💡 InlineActions]` — use `[InlineActions]`).
