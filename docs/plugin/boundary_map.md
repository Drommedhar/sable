# Plugin SDK — Codebase Boundary Map

**Status:** AUDIT (no code changed). Produced for [PLUGIN_SDK_PLAN.md](../../plans/PLUGIN_SDK_PLAN.md) §23 (codebase audit) + §23.2 (boundary map).
**Date:** 2026-06-30. **Scope:** maps Sable's existing extension surfaces, classifies each as *expose / wrap / refactor / keep-internal*, and lists what a safe plugin host must build from scratch.

> One-line verdict: the **engine layer (Document / Layer / Command / UndoStack / GpuCompositor) is clean, headless, and ready to wrap**. Everything user-facing (menus, panels, tools, formats, adjustments, filters) is a **closed enum + switch hard-coded in `MainWindow` or the compositor** — no registry, no DI, no reflection, no plugin loader anywhere. The plugin platform is mostly *new host infrastructure*, not refactoring of existing seams.

---

## 1. §23.2 Boundary map — the four questions

### 1.1 What can already be exposed safely (read + command-mediated write)

| Surface | Where | Why safe |
|---|---|---|
| `Document` read (size, dpi, depth, ICC, selection, guides) | `Sable.Engine/Document.cs` | UI-agnostic POCO, no Avalonia dep |
| `Layer` + subclass read props (opacity/blend/visible/locks/tag/offset/transform/mask/effects/children) | `Sable.Engine/Layers/*` | plain props; setters only flag dirty |
| `Layer.Clone()` | `Layer.cs:131` | deep copy, no side effects |
| Layer mutation via **Commands** | `Sable.Engine/Commands/LayerCommands.cs` (+ `Sable.Tools` raster cmds) | `IUndoableCommand`, undo-aware, structure-signalling — *the* canonical write path |
| `UndoStack.Execute/Undo/Redo/JumpTo` + `History`/`Cursor`/`Changed` | `Sable.Core/Undo/UndoStack.cs` | clean public surface, one per document |
| Pixel I/O: `PixelLayer.SetBuffer/ToBytes/GetTile/SetTile`, `RasterTiles.*`, `ReadComposite()`/`ReadCompositeFloats()` | `Sable.Engine/Layers/{PixelLayer,RasterTiles}.cs`, `GpuSurfaceControl.cs:289` | pure or readback; intended reuse |
| Headless composite: `GpuCompositor.CompositeToBytes/CompositeToFloats` | `Sable.Engine/Compositing/GpuCompositor.cs:1061` | proven headless in `Sable.Gpu.Spike` |
| Enums (`BlendMode`, `AdjustmentKind`, `FilterKind`, `ShapeKind`, `LayerEffectKind`, `ToolKind`) | `Sable.Core` / `Sable.Engine` / `Sable.Tools` | read-only constants |
| Tiled-inference skeleton (model for tiled pixel ops) | `Sable.Ai/Tiling/TileInference.cs` | pure plan→accumulate→finalize |
| Async-op pattern (IProgress + CancellationToken + BusyWindow) | `Sable.Ai/AiService.cs`, `Sable.App/BusyWindow.axaml.cs` | reusable for any long plugin job |

### 1.2 What must stay internal (never raw on the SDK)

- **`Document.Layers` direct list mutation** — bypasses undo + `MarkStructureChanged`. Force Commands.
- **`Layer.Children` / `Layer.Effects` direct list mutation** — no commands exist yet; mutating raw breaks undo. Command-gate (commands TBD).
- **Direct `PixelLayer.Pixels[]` / `Layer.Mask[]` writes** — bypass dirty-tracking + undo. Allowed only behind a transaction that snapshots tiles (`RasterStateCommand` / `PaintRasterCommand`) and calls `Dirty=true`+`MarkTilesDirty`.
- **Raw wgpu device / buffers / pipelines** (`Sable.Gpu`) — ABI-fragile, crash-prone. Never hand a plugin the `Device*`.
- **GPU dispatch internals** (`DispatchBlend`/`DispatchAdjust`/`RenderFilter`, uniform packing, BGL entry counts) — these are the WGSL contract; any drift = `0xC0000409`.
- **`GpuSurfaceControl` Win32/NSView/X11 input internals** — platform seam, not SDK.

### 1.3 What needs refactoring before it can be SDK surface

| Today | Needs |
|---|---|
| Export = `ImageFormat` enum + `switch` across `ImageCodec`/`ExportDialog`/`MainWindow` | `IExportProvider` + `ExportRegistry` (LOW effort — best first seam) |
| Import = Skia auto-detect + hard-coded `.psd` check + hard-coded picker patterns | `IImportProvider` + `ImportRegistry`, dynamic picker filter (MED) |
| `.sable` layer serialize = type `switch` in `SableFile.SaveLayer`/`BuildLayer` + monolithic `LayerDto` | `ILayerSerializer` registry + polymorphic DTO (HIGH — only needed for plugin layer types) |
| Menus = static XAML `<MenuItem Click=...>` | code-built menu from a command registry |
| Command palette = `List<(string,Action)>` rebuilt per open in `MainWindow.OnCommandPalette` | promote to a persistent `ICommandRegistry` (this is the closest thing to a seam today) |
| Hotkeys = `KeyCommands.Catalog` (immutable) + `KeyCommandRun` map | allow registry append |
| Panels/windows = fixed XAML children + `new XxxWindow()` toggles | `IPanelProvider` + dynamic host point |
| Adjustments/Filters/Tools = closed enum + `switch` + **embedded** WGSL | node/tool registry + runtime WGSL load from plugin dir (P2 — hardest) |

### 1.4 What should be capability-gated (maps to plan §10 / §14)

`document.read` → 1.1 Document read · `layer.read` → Layer read props · `layer.write.basic` → Layer commands (Add/Remove/Move/Group/Transform/Offset) · `command.register` → command-palette/menu registry · `automation.batch` → headless `Document`+`UndoStack`+`CompositeToBytes` + `AssetExport` helpers · `export.provider` → `IExportProvider` (after refactor) · `ui.menu_command` → menu registry · `selection.read` → `Document.Selection/SelectionMask` · `pixel.read` → `RasterTiles`/`ReadComposite` · `pixel.write.layer_output` → `PixelLayer.SetBuffer` via `RasterStateCommand` · `ui.panel` → `IPanelProvider` · `undo.transaction` → **needs a new `MacroCommand`** (none today) · `document.events` → **needs new granular events** (only `UndoStack.Changed` exists) · `filter.node`/`generator.node`/`gpu.compute` → node + WGSL registry (P2).

---

## 2. Subsystem audit detail

### 2.1 Document & Layer model — `Sable.Engine`

- **Document** (`Document.cs`): `Width/Height` (private set → `SetSize`), `Dpi`, `Depth`, `IccProfile`/`IccProfileName`, `Selection` (SelRect?), `SelectionMask` (byte[]?), `SelectionVersion`, `GuidesX/Y`, `SavedSelection`, `Layers` (List, bottom→top). Tree nav: `FindParent(layer)`. **Observability is poll-based** (`NeedsComposite`, `AnyDirty`, `MarkStructureChanged`, `ClearDirty`) — **no events**.
- **Layer base** (`Layer.cs`): identity/blend/visibility/clip/locks/colortag/offset/affine(`ScaleX/Y`,`Rotation`,`ShearX/Y`)/perspective(`Perspective`,`PerspCorners[8]`)/`Effects`/`Children`/`Mask`/`BlendIf*`/`SmartObject`. `ContentBounds(docW,docH)`, `Clone()`→`CreateClone()`.
- **Subclasses**: `PixelLayer` (own `Width/Height/OffsetX/Y`, `Pixels` float[] RGBA32F, `SetBuffer`, `ExpandToCover`/`TrimToContent`, tile accessors); `AdjustmentLayer` (15 `AdjustmentKind`, per-kind params, `PackParams`, curve/gradient LUTs); `FilterLayer` (10 `FilterKind`, Radius/Amount/Angle); `GroupLayer` (`PassThrough`); `ShapeLayer` (7 `ShapeKind`, fill/stroke/dash/cap/join + `BuildOutline`/`Rasterize`); `TextLayer` (`BoxWidth`/`Tracking`/`PathPoints`, `ToPath()`); `PathLayer` (bezier `Nodes`+`ExtraContours`, `Flatten`, `Rasterize` via `VectorRaster`).
- **Commands** (all `IUndoableCommand`): Add/Remove/Move/MoveTo/MoveOffset/Transform/Group/GroupLayers/Ungroup/ReplaceLayers/SetMask/Align/EditPath/SetTextPath/RestoreSnapshot (`Sable.Engine/Commands/LayerCommands.cs`); `PaintRasterCommand<T>`/`RasterStateCommand` (`Sable.Tools`).

**SDK note:** layer-creation has no factory — `new PixelLayer(w,h)`. Effects/children/text/selection have **no mutation commands** yet → those P1 capabilities require new commands first.

### 2.2 Command / undo / jobs / threading — `Sable.Core` + app

- `IUndoableCommand { string Name; void Do(); void Undo(); }`; `UndoStack`: `Execute` (truncates redo tail, fires `Changed`), `Undo/Redo`, `JumpTo`, `History`, `Cursor`, `CanUndo/Redo`, `Capacity` (200), `Clear`. **No transaction/macro** — a plugin batching N edits into one undo step must author a composite `IUndoableCommand` itself.
- One `UndoStack` per `DocumentViewModel` per `DocumentTab`; `Vm.Undo.Changed → Resync()` rebuilds the layer VM list + sets tab dirty.
- **Threading: single-threaded on the Avalonia dispatcher.** No locks. Compositor runs on a `DispatcherTimer` at `DispatcherPriority.Input` (~8ms). Commands run sync on UI thread. **Plugins must never mutate `Document` off-thread → `Dispatcher.UIThread.Post(...)`.**
- **Jobs:** AI ops are `async Task` returning an `IUndoableCommand` (or bytes), driven by `IProgress<double>` + `CancellationToken` + modal `BusyWindow`; ONNX runs in-proc (blocks UI, native runtime parallelizes). No general job queue.
- **Headless** proven: `Sable.Gpu.Spike` builds a `Document`, runs commands, `CompositeToBytes`, exports PNG — zero Avalonia. This is the automation/batch substrate.

### 2.3 Import / export — `Sable.Imaging` + `Sable.Format`

- `ImageCodec`: `ImageFormat {Png,Jpeg,Webp,Tiff}` enum; `Extension`/`FormatFromExtension`/`EncodeScaled`/`EncodeScaledFloat` all `switch`. ICC: `InjectPngIccp` + `EncodeTiff(...,icc)` (JPEG/WebP ICC = TODO). Self-contained TIFF + PNG16 encoders.
- `DocumentIO`: `OpenImage` (Skia auto-detect), `Export`/`ExportFloat`/`ExportPng` — pure delegation to `ImageCodec`, no switch of its own.
- `SableFile`: zip (`document.json` + `layers/{i}.raw` deflate + masks + `color.icc` + preview). `SaveLayer`/`BuildLayer` = big per-type `switch` + monolithic `LayerDto`.
- `PsdReader.Load(...)` (+`CompatibilityReport`): hard-coded PSD only; warnings are string-matched.
- UI: `ExportDialog` combo = 4 hard-coded `ComboBoxItem` + positional index `switch`; `MainWindow.OnOpenImage` hard-codes picker patterns + `.psd` branch; `OnBatchExport` similar.
- Composite source: `GpuCompositor.CompositeToBytes/Floats`.

**Best first plugin seam = `IExportProvider`/`ExportRegistry`** (move 4 encoders into providers; data-bind the combo). Import registry second. Layer-serializer registry only when plugin layer types land.

### 2.4 UI extension points — `Sable.App` (≈4400-line `MainWindow`)

- **100% hard-coded.** Menus in XAML (`Click="OnXxx"` / `Command="{Binding RelayCommand}"`). Command palette = list rebuilt per open in `OnCommandPalette`. Hotkeys = immutable `KeyCommands.Catalog` + lazy `KeyCommandRun` map, matched in `OnGlobalKeyDown` (tunnel). Panels = fixed XAML children toggled by `Show*Panel` settings; modeless windows `new XxxWindow{DataContext=...}` per toggle.
- MVVM: `DocumentViewModel` (sealed, `[RelayCommand]`s, `ObservableCollection<LayerViewModel>`, `SelectedLayer`, `Undo`) — observable but **no selection-changed event** (only `Undo.Changed`).
- Reusable controls (`Controls/`): `LabeledSlider`, `HexColorField`, `SettingRow`, `GradientSlider`, `CurveEditor`, `Histogram`, `ColorWheel`, `Ruler`, `ToolButton`, `NavigatorView` — building blocks for a plugin panel.
- **i18n mandatory + build-enforced** (`{loc:Loc key}` / `Loc.T`, `Locales/en.json`+`de.json`, `LocaleDoctor`). Flat key namespace → plugins need an `plugins.<id>.*` prefix convention.

### 2.5 Graph / render / pixel — `Sable.Engine` + `Sable.Gpu`

- `GpuCompositor` headless + reusable; tiling via `TileResidency` LRU + atlas; `ReleaseLayer`/`ReleaseLayerCaches`.
- **Adjustments/Filters/Tools are closed enums + `switch` + embedded WGSL.** Recipe for a new adjustment/filter today = edit enum + `PackParams`/`RenderFilter` + add a `.wgsl` (embedded resource, loaded by name via `ShaderLibrary.Load` → `CreateWgslModule`) + UI + serializer. **A plugin cannot supply a WGSL pass at runtime** — shaders are compile-time embedded resources, dispatch is hard `switch`, uniform layouts are fixed-size. P2 (`filter.node`/`gpu.compute`) needs: runtime shader load from plugin dir + a node registry replacing the enum switch + a stable uniform/binding ABI. Highest-risk capability.
- Pixel processing path is open: read via `RasterTiles`/`ReadComposite`, write via `PixelLayer.SetBuffer` wrapped in `RasterStateCommand`; `TileInference` is the tiling template.
- `ToolKind` (~48) closed enum routed by `if/switch` in `GpuSurfaceControl.Input.cs` — no `ITool` interface; new tools need recompile.

### 2.6 Support: logging / crash / settings / AI / FS — `Sable.Core` + app

- **Logging: none.** Only `Debug.WriteLine` + `Console`. User-facing errors via `ToastWindow.Push` / modal / status bar / `BusyWindow`. **No global crash handler** beyond `App.axaml.cs` try-catch (prints stderr, re-throws). Plugin exceptions today would crash the host → **need error boundaries + an `IPluginLogger`.**
- **Settings:** `SableSettings` POCO (~60 props) + `SettingsService.Load/Save` JSON at `%AppData%/Sable/settings.json`, silent default on corrupt. Dirs: `%AppData%/Sable/{settings.json, models/<id>/, workflows/}`. **No per-plugin settings namespace** → need `plugins.<id>.*` or `%AppData%/Sable/plugins/<id>/`.
- **AI/external:** `AiService` (readiness gate, registry, GPU probe) + `OnnxBackend` (`IMaskModel`/`IRasterModel`) + `ModelDownloader` (**network** — HTTP/HF). Generative sidecar (`Sable.Ai.Sidecar/Ipc/SidecarClient` — localhost HTTP + Bearer) is the **only IPC/out-of-process mechanism** and it's mostly deferred. `IAiBackend.AddBackend` is a manual hook a plugin AI backend could use.
- **FS:** user files via Avalonia `StorageProvider` (sandboxed picker → `IStorageFile`); app data via raw `System.IO` to hard-coded `%AppData%/Sable/`. **No path scoping/sandbox** today.
- **DI/reflection: none.** No MEF, no `IServiceCollection`, no `Assembly.Load`/`AppDomain`, no plugin discovery. Manual `new` + static singletons (`Loc.Instance`, `SettingsService`, `KeyCommands.Catalog`). A plugin loader is greenfield.

### 2.7 Project layout (where new projects slot in)

`Sable.slnx`, all `net10.0`, `Directory.Build.props` (nullable, unsafe, implicit usings). Core→Engine→{Gpu,Imaging}; Canvas/Tools/Format/Ai on Engine; UI(MVVM)→Engine; App→everything. **Proposed additions:** `Sable.Plugin.Sdk` (contracts, refs `Sable.Core` only) + `Sable.Plugins` (host loader/registry/sandbox, refs SDK). App gains a ref + startup load + a manager UI page.

---

## 3. §29-format feature blocks (the P0 candidates)

### Feature: Export providers
- **Current status:** HARD-CODED (no plugin path).
- **Current implementation:** `Sable.Imaging/ImageCodec.cs` (`ImageFormat` enum + `EncodeScaled`/`EncodeScaledFloat` switch); `Sable.App/ExportDialog.axaml(.cs)` (combo + positional switch); `Sable.App/MainWindow.OnExport`; composite from `GpuCompositor.CompositeToBytes/Floats`. No plugin loader exists.
- **User-visible goal:** a third-party plugin adds a new export target with settings UI + progress/cancel.
- **Proposed SDK surface:** capability `export.provider`; `IExportProvider { Id; Label; Extension; SupportsLossy/Alpha/16Bit/Icc; Encode(...); EncodeFloat(...) }` + `ExportRegistry.Register`; manifest `capabilities:["export.provider"]`; permission `filesystem_write`.
- **Safety model:** out-of-process by default (or in-proc trusted P0); permission-gated write; timeout/cancel via `CancellationToken`+`BusyWindow`.
- **Required tests:** loads + registers provider; appears in `ExportDialog` combo; receives expected composite bytes/dims; cancel works; provider throw does not crash host.

### Feature: Automation / batch command
- **Current status:** SUBSTRATE EXISTS, NO HOST.
- **Current implementation:** headless `Document` + `UndoStack.Execute` + layer Commands + `GpuCompositor.CompositeToBytes` + `Sable.Engine/IO/AssetExport` (name/crop helpers); proven by `Sable.Gpu.Spike`. No script runtime, no batch entry point.
- **User-visible goal:** run a headless command over N files (batch export, layer cleanup, QA).
- **Proposed SDK surface:** `command.register` + `automation.batch`; `IPluginCommand { Id; Label; Run(IHostContext) }`; `IHostContext` exposes document/layer read, command execute, file iterate, `IProgress`, cancel, structured log.
- **Safety model:** out-of-process; `filesystem_read/write` scoped; cancel + timeout; each batch item wrapped in one undo macro (needs `MacroCommand`).
- **Required tests:** command registers + lists; batch iterates files; progress/cancel; failure isolates per-item; mutations land on the undo stack.

### Feature: Menu / palette command
- **Current status:** HARD-CODED.
- **Current implementation:** XAML `Menu`; `MainWindow.OnCommandPalette` list; `KeyCommands.Catalog` + `KeyCommandRun`.
- **User-visible goal:** plugin contributes a menu item / palette entry / optional hotkey.
- **Proposed SDK surface:** `ui.menu_command` + `command.register`; promote palette `List` to `ICommandRegistry.Register(id,label,category,gesture?,action)`; menu built from registry; localized via `plugins.<id>.*` keys.
- **Safety model:** in-proc UI thread; action wrapped in try-catch → toast on throw (no host crash).
- **Required tests:** command appears in menu + palette; runs handler; error toasts without crashing UI; optional gesture binds.

### Feature: Plugin host lifecycle (the prerequisite for all of the above)
- **Current status:** DOES NOT EXIST.
- **Current implementation:** none — no loader, registry, manifest, permissions, sandbox, logging, safe-mode.
- **User-visible goal:** discover/load/enable/disable plugins; show permissions; safe-mode; diagnostics.
- **Proposed SDK surface:** `Sable.Plugin.Sdk` (`IPlugin`, manifest schema per plan §16, capability enum, `IHostContext`, `IPluginLogger`, `IPluginSettings`) + `Sable.Plugins` (`PluginLoader` scan `%AppData%/Sable/plugins/*`, `PluginRegistry`, SDK-version + capability negotiation, per-plugin try-catch boundary, quarantine-after-N-crashes).
- **Safety model:** manifest-declared capabilities user-approved at load; safe-mode launch skips all; each call inside an exception boundary; out-of-process for untrusted (in-proc trusted only, signed later).
- **Required tests (mirror plan §28):** load valid; reject bad SDK version; reject malformed manifest; disable after repeated crash; safe-mode skips load; denied permission → clear host error.

---

## 4. Recommended P0 build order (smallest real platform)

1. `Sable.Plugin.Sdk` + `Sable.Plugins`: manifest schema, capability enum, `IPlugin`/`IHostContext`, loader + registry, SDK-version + capability negotiation, per-plugin exception boundary. (No existing code to refactor — pure new.)
2. **Logging + error boundary** (`IPluginLogger`, host try-catch, safe-mode flag) — prerequisite for trusting any plugin call.
3. **`MacroCommand`** in `Sable.Core/Undo` — needed for `undo.transaction` and clean batch undo (does not exist today).
4. **Export seam:** `IExportProvider` + `ExportRegistry`; port PNG/JPEG/WebP/TIFF to providers; data-bind `ExportDialog`. (Lowest-risk refactor, immediate user value.)
5. **Command registry:** promote the palette list to `ICommandRegistry`; menu + palette + hotkeys read from it; expose `command.register`/`ui.menu_command`.
6. **Plugin manager UI** (Settings page): list / enable-disable / permissions / logs / safe-mode.
7. Sample plugins: hello-world command, batch export, export provider. `tests/plugins/` harness per plan §28.

**Defer:** import + layer-serializer registries (P1), panel provider + granular document events (P1), node/tool/WGSL registries (P2), external-tool bridge (P3), PS-compat shims (P4). Keep the canvas fixed-centre (native-HWND airspace) — panel plugins dock in chrome only.

---

## 5. Net-new infrastructure checklist (nothing to reuse)

Plugin loader/registry · manifest validation + SDK-version/capability negotiation · per-plugin exception isolation + quarantine · `IPluginLogger` + structured logging · per-plugin settings namespace · permission model + prompts + manager UI · safe-mode launch · `MacroCommand`/transaction · granular document/selection events · format/command/panel/node registries (replacing the enums+switches) · out-of-process host (reuse the sidecar IPC pattern for untrusted/heavy plugins).
