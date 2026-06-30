# Sable User Manual

Sable is a free, open-source raster image editor for Windows, macOS and Linux. It is **GPU-first** — the image you see is always a live recomputation of your document on the graphics card, never a cached bitmap — and **fully non-destructive**: adjustments, live filters, masks, layer effects and transforms are nodes in the document graph that can be changed, reordered or removed at any time. Its AI tools run entirely on your machine: no cloud service, no account, no telemetry.

This manual describes every feature of the current release in detail. Use the table of contents to jump to a topic.

---

## Installation and requirements

### System requirements

- **GPU** — Sable renders everything on the graphics card through wgpu, which drives DirectX 12 on Windows, Vulkan on Linux and Metal on macOS. Any GPU capable of one of these APIs works; there is no software-rendering fallback for the canvas.
- **Operating system** — Windows 10/11, a modern Linux distribution (X11), or macOS on Apple Silicon.
- **AI tools** (optional) need additional GPU headroom: DirectML on Windows, CUDA on Linux (NVIDIA), or WebGPU/Metal on Apple Silicon. Sable checks free VRAM before every AI operation and refuses with a clear message if a model will not fit — it never silently degrades to a CPU crawl.

### Installing

Download the installer for your platform from the [releases page](https://github.com/Drommedhar/sable/releases) — a `.exe` installer on Windows (associates `.sable` files automatically), a `.dmg` on macOS, an AppImage on Linux (`chmod +x`, then run).

### Updates

Sable checks for updates automatically (disable in **Edit ▸ Preferences ▸ Updates**) and can also check on demand via **Help ▸ Check for Updates**. When an update is found, the update dialog downloads and launches the installer for you. The About window (**Help ▸ About Sable**) shows the exact version, runtime and active GPU renderer.

---

## Getting started

### The welcome screen

With no document open, Sable shows a welcome screen with a grid of your recent files and a **New document** button. It can be disabled in Preferences (**Show welcome screen**) or via the checkbox on the screen itself. On first launch a short tips card introduces the tool strip, canvas navigation, the layers panel and the optional AI features.

### Creating a document

**File ▸ New** (Ctrl+N) opens the New Document dialog:

- **Custom size** — width, height, unit, **portrait/landscape** toggle and DPI.
- **Presets** — searchable, grouped into **Print**, **Screen**, **Social** and **Photo** categories. Each preset shows its pixel dimensions and DPI.

### Opening files

- **File ▸ Open** (Ctrl+O) — opens a `.sable` document.
- **File ▸ Open Image** — imports any raster image (PNG, JPEG, WebP, BMP — anything the codec understands, EXIF rotation honoured) as a new document with one pixel layer.
- **Photoshop documents** — opening a `.psd` (or large-format **`.psb`**) imports the file **with its layer structure**: layers, groups (including pass-through), opacity, fill opacity, blend modes, clipping masks and layer masks are mapped to Sable's equivalents.
  - **Adjustment layers** import as **editable** Sable adjustments (Brightness/Contrast, Levels, Curves, Hue/Saturation, Colour Balance, Black & White, Channel Mixer, Photo Filter, Posterize, Threshold, Gradient Map, Exposure, Vibrance, Invert and more) — not flattened.
  - **Solid-colour fill layers with a vector mask** import as **editable path layers** (fill colour plus Bézier nodes), including compound shapes with holes.
  - **Smart Objects** import as rasterised layers with their placement and source metadata preserved (embedded editing isn't supported yet).
  - **Text and other shape/effect constructs** that can't be mapped exactly are rasterised, and anything simplified (multi-style text, text warp, vertical text, gradient-overlay stops, bevel contours, …) is reported.
  - An embedded **ICC colour profile** is preserved and re-embedded on export.
  - After import, the **Compatibility Report** (Window ▸ Compatibility Report, or *View report* on the import toast) lists everything that came in with reduced fidelity, plus any **missing fonts** referenced by text layers. The imported document is a new untitled tab — your original file is never modified.
- **File ▸ New from Clipboard** — creates a document from the image currently on the system clipboard.
- **File ▸ Place** — inserts an image file into the *current* document as a new layer.
- **File ▸ Open Recent** — your recent files; also shown on the welcome screen.
- **Drag and drop** — drop any supported file onto the Sable window to open it in a new tab.

### Tabs, sessions and recovery

Every document lives in its own tab in the tab strip above the canvas:

- Click a tab to activate it; the **+** button creates a new document.
- Close with the tab's **×**, **Ctrl+W**, or a **middle-click** on the tab. **File ▸ Close All** and **Close Others** are available too.
- A modified document shows a dirty marker; closing it asks for confirmation. **File ▸ Revert** discards all changes and reloads the file from disk (confirmed first).
- Sable restores your **session** on launch: open tabs, window size/position and recent files (toggle with **Reopen last session** in Preferences).
- **Crash recovery** — Sable autosaves open documents in the background (default every 5 minutes, configurable, can be disabled). If the application did not close cleanly, the next launch offers to restore the unsaved documents.

### Saving and exporting

- **File ▸ Save** (Ctrl+S) / **Save As** (Ctrl+Shift+S) write the native `.sable` format, which preserves the complete document graph — see [The .sable format](#the-sable-file-format).
- **File ▸ Export** (see [Exporting](#exporting)) writes flattened PNG / JPEG / WebP.

---

## The workspace

Sable uses a dark, panel-based layout in the style of Photoshop and Affinity Photo. All panels can be toggled from the **Window** menu (Tools Panel, Colour Panel, Layers Panel, Contextual Task Bar).

### Title bar and menus

The application menu is embedded in the window title bar (File, Edit, Image, Layer, Type, Select, Filter, View, AI, Window, Help), with the window controls on the right. Drag the empty title-bar area to move the window; double-click it to maximise.

### Options bar

Directly under the menu, the options bar shows the parameters of the **active tool** — brush size/hardness/flow for the paint tools, fill/stroke/sides for shapes, aspect ratio for crop, sample size for the eyedropper, and so on. It updates as you switch tools.

### Tool strip

The vertical strip on the left edge holds the tools, grouped into flyouts:

| Key | Group |
|---|---|
| **V** | Move / Transform |
| **M** | Rectangle Marquee · Elliptical Marquee |
| **L** | Lasso · Polygonal Lasso |
| **W** | Magic Wand · Colour Range · Smart Select (AI) |
| **B** | Brush · Pencil · Eraser |
| **G** | Fill · Gradient |
| **C** | Crop |
| **U** | Rectangle · Rounded Rectangle · Ellipse · Polygon · Star · Line · Arrow |
| **S** | Clone Stamp · Healing Brush · Spot Heal · Patch |
| **O** | Dodge · Burn · Sponge · Blur · Sharpen · Smudge |
| **Y** | Liquify · Mesh Warp |
| **T** | Text |
| **P** | Pen · Node |
| **I** | Eyedropper |
| **H** | Hand |
| **Z** | Zoom |

Three ways to use the keys:

1. **Press** a letter to activate the group's current tool.
2. **Press again** to cycle through the group (B cycles Brush → Pencil → Eraser, …).
3. **Hold** the letter for a *temporary* tool: the tool is active while the key is down and the previous tool returns on release (e.g. hold Z to zoom mid-paint). Hovering a grouped button also opens a flyout listing the group's members.

### Radial quick menu

Press **`** (backtick) to pop a radial tool menu at the cursor — pick a tool without moving to the strip.

### Right-side panels

- **Colour panel** — tabs for **Color** (wheel + sliders), **Gradients** (gradient preset editor), **Swatches**, **Histogram** and **Navigator** (miniature of the document; drag to pan the view).
- **Layers panel** — tabs for **Layers**, **Channels** and **Paths**:
  - **Layers** — the document tree with all per-layer controls, a **filter box** to search layers by name, and a footer with New Layer / Mask / Adjustments / Live Filters / Group / Delete buttons. Parameter panels for the selected adjustment, filter or shape dock below the list.
  - **Channels** — Red, Green, Blue, Alpha and composite RGB rows with visibility toggles; **right-click a channel row to load it as a selection** (luminance selection).
  - **Paths** — the vector paths in the document.

### Contextual task bar

A floating pill appears under an active selection with one-tap actions: **Gen Fill** (when generative AI is enabled), **Mask** (add/remove a layer mask from the selection), **Invert** and **Deselect**. Toggle it via Window ▸ Contextual Task Bar.

### Status bar

The bottom strip shows, left to right: **Fit (0)** and **1:1** buttons, the zoom percentage (editable), document dimensions and DPI, colour mode and bit depth, the colour under the cursor (with a live swatch), cursor coordinates in document pixels, current selection size, and the application's memory / GPU VRAM usage.

### Themes and appearance

**Edit ▸ Preferences ▸ User Interface**:

- **Theme** — Dark, Gray or Light (Dark and Gray are complete; Light is still being tuned).
- **Language** — switches the interface language immediately.
- **Canvas overlay colours** — the guide, smart-guide, grid and quick-mask colours are each customisable.
- **Transparency checker** — cell size and both checker colours.
- **Precise cursor** — adds a crosshair at the centre of the brush cursor.
- **Accent colour** — the highlight colour used across the UI.

---

## Navigating the canvas

| Action | Input |
|---|---|
| Zoom in / out at the cursor | Mouse wheel |
| Zoom keys | **+** / **-** (also Ctrl+Plus / Ctrl+Minus) |
| Fit document to window | **0** or Ctrl+0, or the **Fit** button |
| 100% pixel view | Ctrl+1, or the **1:1** button; View menu also has 50% / 200% |
| Pan | Middle-mouse drag, **Space**-drag with any tool, arrow keys, or the Hand tool (H) |
| Zoom tool | **Z** — click zooms in, Alt+click zooms out |
| Navigator panel | Drag the view rectangle to pan |

Outside the document a checkerboard is drawn (the same checker shows through transparent pixels). Zoomed in past 100%, pixels render crisp (nearest-neighbour), and at high zoom an automatic **pixel grid** outlines individual pixels (toggle: View ▸ Show Pixel Grid).

### Rulers, guides, grid and snapping

- **Rulers** (View ▸ Show Rulers) run along the top and left edges in document pixels and track the viewport live.
- **Guides** — click a ruler to drop a guide at that position. Drag a guide to move it, drag it off the canvas to delete it, or remove all at once with **View ▸ Clear Guides**.
- **Smart guides** appear automatically while moving layers, indicating alignment with the edges, centres and midpoints of other layers and of the canvas.
- **Grid** — View ▸ Show Grid; **View ▸ Grid Settings** configures spacing, subdivisions, colour and grid snapping.
- **Snapping** — **View ▸ Snapping** opens the snapping options: master enable, screen-pixel tolerance, snap to grid / guides / canvas edges and centre, snap to other objects' bounding boxes, and whether hidden layers count as snap targets. View ▸ Snap to Guides/Grid is the quick toggle.

---

## Documents

### Colour mode and bit depth

**Image ▸ Mode** switches the document depth between **8-bit**, **16-bit** and **32-bit (float)** per channel. Sable composites in linear float colour regardless, and now **edits and stores at the chosen depth end to end** — 16- and 32-bit documents keep their full precision through painting, adjustments and filters, and 16-bit PNG and TIFF import and export at full depth. The status bar shows the current mode.

### Resizing

- **Image ▸ Resize Document** — scales the image. Width/height fields with **aspect-ratio lock**, unit selection, DPI, and a **Resample** toggle: with resample on, pixels are interpolated to the new size; with it off only the DPI/print size changes.
- **Image ▸ Resize Canvas** — changes the canvas size *without* resampling. A 9-point **anchor** selector controls where the existing content sits in the new canvas. Because layers keep their own bounds, enlarging the canvas later can reveal content that was previously outside it.

### Cropping

The **Crop tool (C)** trims the canvas:

- Drag a rectangle; refine it with its handles; **Enter** commits, **Esc** cancels.
- **Aspect** — free, or a fixed ratio chosen in the options bar.
- **Delete pixels** (options-bar checkbox) — when off (default), layers keep their full content and the crop only shrinks the canvas: enlarging the canvas later "un-crops". When on, pixels outside the crop are discarded.

---

## Layers

A Sable document is a **tree of layers** composited bottom-to-top on the GPU. The on-screen image is always a live recomputation of this tree — nothing is flattened behind your back, which is what makes every property editable and undoable at any time.

### Layer types

| Type | Description |
|---|---|
| **Pixel layer** | Raster RGBA pixels — what the paint tools edit. |
| **Group** | A folder of child layers, composited in isolation, then blended as a unit with the group's own opacity/blend/mask — or set to **pass-through**, where children composite directly onto the layers below the group (so an adjustment inside the group affects everything underneath it). |
| **Adjustment layer** | A non-destructive colour adjustment (Curves, Levels, HSL, …) applied to everything below it. Carries no pixels. |
| **Live filter layer** | A non-destructive filter (Gaussian Blur, Sharpen, …) applied to everything below it. |
| **Shape layer** | An editable vector shape — rectangle, rounded rectangle, ellipse, polygon, star, line, arrow. |
| **Text layer** | Editable text: point text, word-wrapped area text, or text on a path. |
| **Path layer** | A cubic-Bézier vector path with fill and stroke, edited with the Pen/Node tools. |

### Per-layer properties

Independent of type, every layer has:

- **Name** — double-click or right-click ▸ Rename.
- **Visibility** — the eye toggle.
- **Opacity** (0–100%) — applied to the layer's final result.
- **Fill opacity** — scales only the layer's *own* pixels while layer effects keep full strength (Photoshop "Fill"). A layer at Fill 0% with a drop shadow shows only the shadow.
- **Blend mode** — see [Blend modes](#blend-modes).
- **Clip to layer below** — restricts the layer to the alpha of the layer beneath it (clipping mask). Indicated on the row.
- **Locks** — three independent toggles: **Pos** (position/transform locked), **Pix** (pixels locked: paint and fill blocked), and **transparency lock** (painting changes colour but preserves the alpha channel).
- **Colour tag** — one of 8 colours, shown as a strip on the row, for visual organisation.
- **Mask** — optional per-layer mask (see [Masks](#masks)).
- **Effects** — an ordered list of layer effects (see [Layer effects](#layer-effects-fx)).
- **Transform** — non-destructive offset, scale, rotation, shear and perspective (see [Move and Transform](#move-and-transform-v)).

### Organising the tree

- **New pixel layer** — footer button or Layer ▸ New Layer.
- **Select** — click a row; **Ctrl+click / Shift+click** multi-select. Multi-selections drag, group, align and delete together.
- **Reorder** — drag rows; a blue line shows the insertion point. Dropping onto the *middle* of a group row moves the layer **into** the group. Dragging several effect layers onto a content layer **nests** them inside it; dragging layers onto another content layer offers to auto-group.
- **Group** (Ctrl+G) / **Ungroup** (Ctrl+Shift+G) — wrap the selection in a group / dissolve it. Groups nest arbitrarily deep; the chevron collapses/expands children.
- **Nested effect layers** — with a pixel/shape/text layer selected, adding an adjustment or live filter nests it *inside* that layer, so it affects **only** that layer (the Affinity model). Added with a group or nothing selected, it applies to everything below it instead.
- **Duplicate** — Ctrl+J (deep copy: pixels, mask, effects, children).
- **Merge Down** (Ctrl+E) — flatten the selected layer into the one below.
- **Merge Visible** (Ctrl+Shift+E) — flatten all visible layers into one.
- **Stamp Visible** (Ctrl+Shift+Alt+E) — like Merge Visible but adds the result as a new layer, keeping the originals.
- **Flatten Image** — collapse the whole document to one layer.
- **Rasterise** — convert a vector/text/adjustment construct to a plain pixel layer.
- **Layer context menu** (right-click a row) — Rename, Duplicate, Rasterise, Delete, Group/Ungroup, Merge/Stamp/Flatten, Add/Remove Mask, **Copy / Paste / Clear Effects**, Transform and Align submenus.

Every one of these operations is a single undo step.

### Dynamic layer bounds

Pixel layers are not pinned to the canvas size. A pasted or placed image keeps its own dimensions and position; painting automatically grows the layer to cover the canvas, and when the stroke ends the layer trims itself back to the tight bounds of its actual content. Pixels pushed off-canvas are preserved — move the layer back (or enlarge the canvas) and they reappear. Memory stays proportional to real content even in very large documents.

---

## Blend modes

Sable ships the complete set of 30 blend modes — the full Photoshop list plus the Affinity extras — selectable per layer (including groups, adjustments and live filters):

| Group | Modes |
|---|---|
| Normal | Normal |
| Darken | Darken · Multiply · Color Burn · Linear Burn · Darker Color |
| Lighten | Lighten · Screen · Color Dodge · Add (Linear Dodge) · Lighter Color |
| Contrast | Overlay · Soft Light · Hard Light · Vivid Light · Linear Light · Pin Light · Hard Mix |
| Comparative | Difference · Exclusion · Subtract · Divide |
| Component | Hue · Saturation · Color · Luminosity (implemented per the W3C compositing spec — results match other professional editors) |
| Affinity extras | Average · Negation · Reflect · Glow · Erase |

### Blend If (blend ranges)

In the **Effects window** each layer has **Blend If** controls: hide the layer where the *underlying* layers are darker or brighter than chosen luminance thresholds. Four sliders define the ranges — shadows fully-hidden / fully-visible and highlights fully-visible / fully-hidden — and the gaps between each pair create smooth feathered transitions instead of hard cutoffs. Classic uses: drop a texture only into the highlights, or make a colour grade spare the deepest shadows.

---

## Masks

Any layer — including groups, adjustments and live filters — can carry a **mask**: a grayscale image in which white shows the layer and black hides it. On adjustments and filters the mask controls *where* the effect applies and at what strength.

- **Add / remove** — the Mask footer button (or right-click ▸ Add/Remove Mask). With an active selection, the contextual task bar's **Mask** button creates a mask directly *from the selection*.
- **Paint a mask** — press **K** to toggle mask-edit mode, then use the normal brush: black hides, white reveals, grays are partial. Mask strokes use the full brush engine and are undoable like any stroke.
- **Mask from Paste Into** — Edit ▸ Paste Into pastes a new layer whose mask is the current selection.
- **AI masks** — AI ▸ Remove Background writes its alpha matte as a regular, editable layer mask.

### Quick mask

Press **Q** to enter quick-mask mode: the current selection appears as a translucent red rubylith overlay that you can refine by painting (paint = select, erase = deselect). Press **Q** again to convert the painted overlay back into a selection. The rubylith colour is customisable in Preferences ▸ UI.

---

## Selections

Selections restrict painting, filling, copying, filters and AI operations to a region of the document.

### Selection tools

- **Rectangle / Elliptical Marquee (M)** — drag to select; a plain rectangle keeps live grips so you can move and resize it on-canvas before committing. Drag the interior to move the selection outline.
- **Lasso (L)** — freehand selection.
- **Polygonal Lasso (L again)** — click to place vertices; click the first point or press **Enter** to close, **Esc** to cancel.
- **Magic Wand (W)** — selects *contiguous* pixels of similar colour; tolerance in the options bar.
- **Colour Range (W again)** — selects **all** similar pixels in the layer, contiguous or not.
- **Smart Select (W again)** — AI hover-to-select; see [AI tools](#ai-tools).
- **Channels panel** — right-click a channel row to load its luminance as a selection.

### Combining selections

All selection tools share the standard modifiers, read at the start of the gesture:

| Modifier | Result |
|---|---|
| *(none)* | Replace the selection |
| **Shift** | Add |
| **Alt** | Subtract |
| **Shift+Alt** | Intersect |

### The Select menu

- **Select All** (Ctrl+A), **Deselect** (Ctrl+D), **Invert** (Ctrl+Shift+I).
- **Grow… / Shrink…** — expand or contract the selection by N pixels.
- **Smooth…** — round off jagged edges and remove stray specks.
- **Border…** — turn the selection into a band of N pixels straddling its edge.
- **Feather…** — soften the selection edge with a gradual falloff (essential before deleting or colour-correcting, to avoid visible seams).
- **Save Selection / Load Selection** — store the current selection with the document and recall it later.

### What selections affect

Painting, erasing, filling, gradients, retouch strokes and Edit ▸ Fill are clipped to the selection; Copy/Cut/Paste operate on the selected region; AI ▸ Remove Object and Generative Fill use it as the target region. The status bar shows the selection's size.

---

## Painting

### The brush engine

The **Brush (B)** paints smooth, interpolated strokes of the foreground colour. Its full parameter set lives in the options bar:

- **Size** — dab diameter ( **[** and **]** shrink/grow it from the keyboard).
- **Hardness** — edge softness of each dab.
- **Flow** — how much paint each dab deposits; low flow builds up gradually across overlapping dabs.
- **Smoothing** — a stroke stabiliser that irons out hand jitter; higher values give a steadier but laggier line.
- **Spacing** — dab spacing as a percentage of brush diameter (0 = continuous).
- **Blend** — the per-stroke paint blend mode (paint with Multiply, Screen, … without changing the layer's mode).
- **Alpha** — the Colour panel's alpha slider scales overall stroke opacity.

**Brush dynamics** (the dynamics popover in the options bar):

- **Angle** and **Roundness %** — squash the round tip into an ellipse and rotate it, for calligraphic strokes.
- **Size jitter / Flow jitter / Scatter / Angle jitter %** — per-dab randomisation for organic, textured strokes.
- **Tip** — the computed round tip, or a **sampled tip** (a bitmap brush shape). **Import .abr brushes** loads Photoshop brush files; their tips and settings become Sable presets. *Clear tip* returns to the round tip.

**Pressure** (graphics tablets): toggles in the options bar map stylus pressure to **size** and/or **flow**.

**Presets** — save the current configuration (size, hardness, flow, spacing, dynamics, pressure) as a named preset in the options-bar preset list; delete from the same place. Imported `.abr` brushes appear here.

**On-canvas HUD**: hold **Ctrl+Alt** and drag — horizontally for size, vertically for hardness. The preview ring stays anchored at the drag-start point so you can dial the brush in without leaving the canvas.

**Live preview**: the dab under the cursor is composited *into* the active layer's pixels while you hover, so the preview honours the layer's blend mode, opacity and everything below — not a fake overlay. The brush cursor shows the true dab outline (add a centre crosshair with Preferences ▸ Precise cursor).

**Quick colour**: **Alt+click** samples a colour with any paint tool. **X** swaps foreground/background; **D** resets them to black/white.

### Pencil

The **Pencil** (B group) is a hard, aliased brush — every pixel either fully painted or untouched. Ideal for pixel art and crisp single-pixel lines. It shares the brush options.

### Eraser (B group, or E)

The **Eraser** removes alpha with the same engine (size/hardness/flow/HUD/dynamics), revealing what is below. With the layer's transparency lock on, painting preserves alpha instead — paint "inside" the existing shape.

### Flood fill (G)

The **Fill tool** flood-fills the clicked region with the foreground colour using a contiguous-colour tolerance (options bar). Respects selections and pixel locks; one undo step. From the keyboard: **Edit ▸ Fill with Foreground** (Alt+Backspace) and **Fill with Background** (Ctrl+Backspace) fill the whole layer or selection.

### Gradient (G again)

The **Gradient tool** drags a gradient from start point to release point (Shift constrains the angle). Options:

- **Shape** — **Linear**, **Radial**, **Conical**, **Reflected** or **Diamond**.
- **Gradient stops** — edited in the **Gradients panel**: click the bar to add a stop, drag to move it, the wheel sets the stop's colour, and stops can be added/deleted with the panel buttons. Gradient presets can be stored and reused.

### Eyedropper (I)

- Click to sample a colour into the foreground swatch.
- **Sample size** — point sample, 3×3 or 5×5 average (options bar).
- **All layers** — sample the composited image you see instead of only the active layer.

---

## Retouching

### Repair tools (S group)

- **Clone Stamp** — **Alt+click** sets the source point; painting then copies pixels from the source, with the offset locked to your first stroke.
- **Healing Brush** — like Clone, but **tone-matched**: the source *texture* is copied while the destination's colour and luminosity are preserved, so repairs disappear into their surroundings.
- **Spot Heal** — one-stroke healing with an automatically chosen nearby source. No Alt+click needed; perfect for dust, blemishes and small defects.
- **Patch** — make a selection, then drag it over a clean area: the entire selected region is healed from there in one tone-matched operation.

### Tone and detail brushes (O group)

Six classic darkroom brushes, each with a **Strength** setting in the options bar:

- **Dodge** — lighten where you paint.
- **Burn** — darken.
- **Sponge** — desaturate.
- **Blur** — soften detail locally.
- **Sharpen** — boost local contrast.
- **Smudge** — push colour along the stroke direction, like dragging a finger through wet paint.

All retouch strokes are single undoable edits and respect selections.

---

## Warping

### Liquify (Y)

An interactive displacement brush. Modes (options bar): **Push** (smear along the drag), **Bloat** (inflate from the brush centre), **Pucker** (pinch inward) and **Twirl** (rotate around the centre), with adjustable brush size and **Strength**. Liquify edits the active pixel layer; each gesture is one undo step.

### Mesh Warp (Y again)

Lays a control grid over the active layer's content. Drag any grid point to bend the image smoothly between points; **Enter** applies the warp as one undoable edit, **Esc** cancels.

---

## Move and Transform (V)

Move and Transform are one tool. With the **Move tool** active:

- Dragging a layer moves it — the offset is **non-destructive**, stored on the layer.
- On a pixel layer a full transform **gizmo** appears: corner handles, edge handles and a rotation handle.

### Gizmo reference

| Drag | Result |
|---|---|
| Corner / edge handle | **Uniform scale**, anchored at the opposite handle |
| + **Shift** | Non-uniform scale (dragged axis only) |
| + **Ctrl** | Scale from the layer centre |
| + **Ctrl+Shift** | Non-uniform from the centre |
| Top handle | **Rotate** — Shift snaps to 15° steps |
| Interior | **Move** — Shift constrains to an axis |
| **Alt** + corner | **Perspective / free distort** — each corner moves independently |

Modifiers are read live each frame, so pressing or releasing them mid-drag works. The full transform — offset, scale, rotation, **shear** and **perspective** — is stored non-destructively and can be edited or zeroed at any time.

### Numeric transform

The **Transform panel** (Window ▸ Transform, auto-shown while transforming) gives numeric fields for offset, scale %, rotation and shear, applied live, plus a Reset button.

### Menu transforms, align and distribute

- **Layer ▸ Transform** — Flip Horizontal / Vertical, Rotate 90° CW / CCW, Rotate 180°, Reset Transform. Each is one undo step.
- **Layer ▸ Align** — align two or more selected layers on any edge or centre line; **Distribute Horizontally / Vertically** spaces three or more evenly. Alignment uses real content bounds, not layer rectangles.

---

## Adjustment layers

Add adjustments from the **half-circle button** in the Layers panel footer (or the layer context menu). With a content layer selected the adjustment **nests inside it** and affects only that layer; otherwise it affects everything below it. Each adjustment has its own opacity, blend mode and optional mask — the mask controls where and how strongly it applies.

Parameters appear in the **Adjustment panel** docked below the layer list (or floating via Window ▸ Adjustments), with **Reset** in the header and opacity + blend mode in the footer. Hold the **Compare** button — or hold **\\** anywhere — to see the image with all adjustments and live filters temporarily hidden (before/after).

| Adjustment | What it does |
|---|---|
| **Brightness / Contrast** | Linear brightness and contrast. |
| **Levels** | Black point, white point and gamma per the histogram (drawn live behind the sliders), plus **output** black/white remapping to lift or compress the range. |
| **Curves** | Free-form curve editor for the RGB composite and the individual R / G / B channels. Click the curve to add a point, drag to shape, right-click a point to delete; the channel's histogram is drawn behind the curve. The most precise tonal tool in the box. |
| **HSL** | Hue shift, saturation and lightness. |
| **White Balance** | Temperature and tint. |
| **Black & White** | Per-channel R/G/B luminance weights for fully controlled monochrome conversion. |
| **Colour Balance** | Independent R/G/B shifts for shadows, midtones and highlights. |
| **Channel Mixer** | A full 3×3 matrix: each output channel as a weighted mix of the input channels. |
| **Shadows / Highlights** | Recover shadow detail and tame highlights independently. |
| **Gradient Map** | Maps image luminance through a colour gradient (editable stops — add, move, recolour, delete) for duotones, false colour and stylised grades. |
| **Exposure** | Exposure in photographic stops. |
| **Vibrance** | Saturation boost that protects already-saturated colours. |
| **Threshold** | Hard black/white cut at a luminance level. |
| **Posterise** | Quantises each channel to N levels. |
| **Invert** | Inverts RGB (no options). |

---

## Live filter layers

Live filters are non-destructive filter nodes: the layers below are *shown* filtered, but their pixels are never touched — change the radius, mask it, reorder or delete it at any time. Add them from the **Live Filters flyout** in the footer or the **Filter menu**. Opacity and mask act as a crossfade between original and filtered result. Like adjustments, a live filter added with a content layer selected **nests** and filters only that layer.

| Filter | Parameters |
|---|---|
| **Gaussian Blur** | Radius — smooth, high-quality blur |
| **Box Blur** | Radius — faster, boxier look |
| **Motion Blur** | Length, angle — directional streaking |
| **Zoom Blur** | Amount — radial streaking from the centre |
| **Sharpen** | Amount — convolution sharpening |
| **Unsharp Mask** | Radius + amount — the classic controllable sharpener |
| **High Pass** | Radius — edge isolation; combine with Overlay/Soft Light blend for frequency-separation sharpening |
| **Clarity** | Radius + amount — local midtone contrast ("punch") |
| **Add Noise** | Amount — film-grain style noise |
| **Denoise** | Radius + amount — edge-preserving (bilateral) noise reduction |

Hold **\\** to compare with all filters hidden.

---

## Layer effects (FX)

Layer effects are non-destructive decorations rendered around a layer's content. Open the **Effects window** with the **fx** footer button (or Window ▸ Layer Effects). It is an Affinity-style master–detail editor: the left list enables and **reorders** effects (stacking order up/down buttons); the right panel edits the selected effect. Every effect has its own colour, opacity and blend mode. The window also hosts the layer's **Fill Opacity** slider and the **Blend If** ranges.

| Effect | Parameters |
|---|---|
| **Outer Shadow** | Colour, opacity, blur radius, X/Y offset — drop shadow behind the layer |
| **Outer Glow** | Colour, radius — soft glow radiating outward |
| **Inner Shadow** | Colour, radius, offset — shadow inside the edges |
| **Inner Glow** | Colour, radius — glow inside the edges |
| **Outline (Stroke)** | Colour, width, position (outside / inside / centre) — follows the layer's exact alpha, not its bounding box |
| **Colour Overlay** | Colour, blend, opacity — tint the layer |
| **Gradient Overlay** | Two colours, angle, start/end — linear gradient clipped to the layer |
| **Bevel / Emboss** | Highlight + shadow colours, size, depth, light angle — lit edge relief |

Effects combine with **Fill opacity** (keep the shadow, hide the pixels) and can be moved between layers with the context menu's **Copy Effects / Paste Effects / Clear Effects**.

---

## Vector tools

### Pen (P)

Draws cubic-Bézier paths:

- **Click** — corner node. **Click-drag** — smooth node with mirrored handles.
- **Click the first node** or **Enter** — commit: a closed path becomes a filled shape, an open path a stroked line (current brush colour). **Esc** cancels.
- The path spine, anchors and handles are previewed live while drawing.

### Node (P again)

Edits the selected path layer:

- **Drag an anchor** to move it; **drag a handle** to reshape — smooth nodes mirror the opposite handle, **Alt** breaks the mirror to create a cusp.
- **Alt+click an anchor** deletes it; **click directly on the path** inserts a node there without changing the curve's shape.

Each gesture is one undo step.

### Shapes (U)

Seven shape tools cycle on **U**: **Rectangle, Rounded Rectangle, Ellipse, Polygon, Star, Line, Arrow**. Drag to create a shape layer (Shift constrains the line/arrow angle). Shapes stay editable vectors:

- **Fill** — on/off + colour.
- **Stroke** — on/off, colour, width, **dash** pattern (dash/gap lengths), **cap** (butt / round / square) and **join** (miter / round / bevel).
- **Per-kind parameters** — corner radius (rounded rect), sides (polygon), points + inner % (star).

Defaults for new shapes come from the options bar; an existing shape is edited in the **Shape panel** that docks in when it is selected.

### Text (T)

- Click to place a **point text** layer and type; double-click existing text to edit. Enter commits (Shift+Enter inserts a line break), Esc commits and exits.
- **Formatting** (options bar): font, **font size**, **Bold / Italic / Underline / Strikethrough**, paragraph **alignment** (left/centre/right), **line spacing %**, **tracking** (letter spacing in px).
- **Area text** — set **Box W** (wrap width) to make the text word-wrap; 0 = point text.
- **Text on a path** — with a text layer selected and a path or shape in the document, **Type ▸ Fit Text to Path** flows the glyphs along the topmost path, each rotated to the local direction. **Type ▸ Detach Text from Path** undoes it. The path shape is baked at fit time — rerun Fit after editing the path.
- **Type ▸ Convert to Curves** turns text into a fully editable path layer; letter counters (the holes in "O", "a") are preserved as proper sub-paths.
- Missing fonts in opened documents raise a notification listing what was substituted.

---

## Colour

The **Colour panel**:

- **Colour wheel** — HSV wheel picker.
- **Slider modes** — switch the slider rows between **RGB**, **HSL**, **CMYK** and **LAB**, each with numeric entry.
- **Alpha** — global paint opacity.
- **Foreground / background swatches** — **X** swaps, **D** resets to black/white.
- **Swatches tab** — click to pick, **+ Add swatch** stores the current colour.
- **Gradients tab** — the gradient stop editor used by the Gradient tool and Gradient Map (click bar = add stop, drag = move, wheel = stop colour).
- **Histogram tab** — live RGB histogram of the composite.
- **Navigator tab** — overview thumbnail; drag to pan.

The status bar continuously shows the colour under the cursor with a swatch, plus cursor coordinates and selection size.

---

## Clipboard

| Command | Shortcut | Behaviour |
|---|---|---|
| Copy | Ctrl+C | Selected region of the active layer (mask-clipped); whole layer if no selection |
| Cut | Ctrl+X | Copy, then clear the selection's pixels |
| Copy Merged | Ctrl+Shift+C | The flattened composite of the selected region |
| Paste | Ctrl+V | New layer at the source position and size |
| Paste In Place | Ctrl+Alt+V | New layer at exactly the original coordinates |
| Paste Into | Ctrl+Shift+V | New layer masked by the current selection |
| Duplicate Layer | Ctrl+J | Deep copy of the selected layer |

Copy also writes the pixels to the **system clipboard** as an image, and Paste falls back to the system clipboard when Sable's internal clipboard is empty — images move freely between Sable and other applications in both directions.

---

## Undo, history and snapshots

- **Ctrl+Z** undo, **Ctrl+Y** / **Ctrl+Shift+Z** redo. *Everything* shares one per-document undo stack — paint strokes, layer operations, masks, transforms, adjustments, AI results. The stack depth is configurable (Preferences ▸ Performance, default 256 steps).
- **History panel** (Window ▸ History) — every step of the session by name; click an entry (or **drag to scrub**) to jump the document to that state.
- **Snapshots** — **Add Snapshot** captures a named copy of the entire layer tree; restore it later from the Snapshots list. Restoring is itself undoable.
- **Autosave** — open documents are checkpointed in the background for crash recovery (interval configurable; see [Tabs, sessions and recovery](#tabs-sessions-and-recovery)).

## Command palette

**Ctrl+K** opens the command palette: type a few letters to fuzzy-search every command — file, edit, selection, layer, transform, align, tool and window actions — and press Enter to run. The fastest route to anything. (The radial menu on **`** is its on-canvas sibling for tools.)

---

## AI tools

Sable's AI runs **entirely on your machine**. No image ever leaves your computer. AI is **off by default** and split into two independent tiers.

### The light tier (built in, ONNX)

Enable under **Edit ▸ Preferences ▸ Machine Learning**:

1. Switching AI on starts the **licence walkthrough**: each recommended model's *original* licence is shown in full, one at a time — you must scroll to the bottom before Accept enables. Accepted models download with progress; declined ones are simply skipped and stay listed in the panel with an **Install** button for later.
2. The **AI menu** appears. Each menu item shows only when a model for its task is installed.

Model weights are **never bundled** — Sable ships download pointers only and always shows the licence first. Turning AI off removes the downloaded models.

| Feature | What it does |
|---|---|
| **AI ▸ Remove Background** | Computes a precise alpha matte for the selected pixel layer and applies it as a normal, editable, undoable **layer mask**. |
| **AI ▸ Select Subject** | Segments the main subject of the layer into a selection. |
| **Smart Select** (W tool group) | Hover-to-select: the active layer is segmented into objects once, then the object under the cursor highlights — blue stripes (replace), green with **Shift** (add), red with **Alt** (subtract). Click commits. The **quality** setting (Preferences ▸ ML: Auto / Fast / Balanced / Thorough) trades object-detection density against GPU load; Auto picks a safe level from your VRAM. If the GPU cannot run it, Sable falls back to CPU for this feature and remembers that. |
| **AI ▸ Upscale** | Super-resolution upscaling of the selected layer, processed in tiles with live progress and Cancel; the result lands on a new layer above the original. |
| **AI ▸ Remove Object** | Make a selection (Smart Select pairs perfectly), run Remove Object: the region is inpainted from its surroundings and the selection cleared. |
| **AI ▸ Models** | The model manager — see below. |

### The generative tier (experimental, opt-in)

A separate switch in Preferences ▸ Machine Learning enables **Generative Fill / Edit / Text-to-Image**, driven by a **local ComfyUI** instance Sable runs in the background:

- **Setup** — on enabling, Sable asks whether you already have a ComfyUI install: if yes, its models (and, when compatible, its Python environment) are **reused in place, never copied**. Otherwise Sable installs its own runtime (PyTorch + ComfyUI — a multi-gigabyte download into Sable's folder). Nothing is installed until you switch the feature on.
- **Presets** (AI ▸ Models ▸ Generative tab) — a generative preset bundles a **base model** + **text encoder(s)** + **VAE** + a **workflow file** (a ComfyUI "API format" `.json` export — required; Sable runs *your exact graph*). Only configured presets appear in Generative Fill, so you control which models are usable. Presets can be flagged **text-to-image** (no input image; output becomes a new document).
- **AI ▸ Generative Fill** — select a region, open Generative Fill: choose a preset, optionally add **compatible LoRAs**, write a **prompt** and **negative prompt**, set **steps**, **CFG scale**, **denoise** (0–1), **seed** (-1 = random) and optional **CPU offload** for models larger than your VRAM — then Generate. The result arrives as a new layer; everything stays undoable. The contextual task bar's **Gen Fill** button is the shortcut.
- **AI ▸ Generate Image** — text-to-image into a new document.

### Models window (AI ▸ Models)

- **ONNX tab** — recommended models per task (**Background removal, Smart selection, Upscale, Object removal, Denoise**) with one-click Download (licence shown first) or Remove, a **Download recommended set** button, a paste box for a **direct URL or a Hugging Face `owner/repo/path/file.onnx`** reference to install your own, and a per-task **default model** selector. The **model folder** is configurable; moving it offers to migrate installed models.
- **Generative tab** — generative presets (above) plus **model sources**: add ComfyUI-style folders whose models are referenced in place.
- Every row shows whether the model **fits your free VRAM**.

### GPU policy

AI inference is GPU-only by design (DirectML / CUDA / WebGPU-Metal). Sable checks the free VRAM before each operation and refuses with a clear message rather than degrading silently (the documented exceptions: models incompatible with the GPU runtime, and the Smart Select CPU fallback, both explicit). Long operations run in a progress dialog with Cancel.

---

## Exporting

**File ▸ Export** opens the export dialog:

- **Format** — **PNG** (lossless), **JPEG**, **WebP** or **TIFF**, plus any **formats added by plugins**.
- **Quality** — 1–100% for the lossy formats.
- **Scale** — export at a percentage of the document size; the resulting pixel dimensions and an **estimated file size** update live.

The export is the flattened composite, rendered on the GPU. PNG and TIFF export at the document's bit depth (16-bit where applicable), and an embedded ICC profile is re-embedded.

### Export Assets (batch)

**File ▸ Export Assets** exports several layers in one action: tick the layers to export, pick a format and one or more **scale variants** (0.5× / 1× / 2× / 3×, each with its own filename suffix), optionally **trim each layer to its content**, choose an output folder, and Export. Every selected layer is written at every chosen scale; colliding filenames are disambiguated automatically.

For lossless interchange of the *layered* document, use `.sable` (or keep the source `.psd` you imported — Sable never overwrites it).

---

## The .sable file format

`.sable` is an open zip container: a JSON description of the document plus raw layer data. It round-trips the **complete** document graph:

- the full layer tree — groups (including pass-through), nested effect layers, clipping;
- every layer's pixels (with their own bounds and offsets), opacity, fill opacity, blend mode, locks, colour tag, visibility;
- masks; layer effects with all parameters and Blend If ranges;
- adjustment and live-filter parameters, including curve points and gradient stops;
- non-destructive transforms — offset, scale, rotation, shear, perspective;
- vector data — path nodes and sub-paths, shape parameters, stroke caps/joins/dashes;
- text content, formatting, box width, tracking and text-on-path data;
- saved selections and document settings (size, DPI, bit depth).

A document saved and reopened is exactly the document you left.

---

## Plugins

Sable can be extended with **plugins** — small add-ons that contribute new commands, menu items, file formats and batch operations, and can read and edit your document. Plugins are **off by default** and run inside Sable with full trust, so only enable ones you trust.

### Enabling and managing plugins

Everything lives in **Edit ▸ Preferences ▸ Plugins**:

- **Enable plugins** — the master switch (takes effect immediately).
- **Install from Folder… / Install from .zip…** — point Sable at a plugin (a folder, or a `.zip`, containing the plugin's `manifest.json` and its files); it is copied into your plugins folder and loaded.
- **Approve access** — before a plugin runs, Sable shows exactly what it requests (its **capabilities** and **permissions**) and asks you to **Allow** or **Don't allow**. A plugin that is later updated to request *more* access asks again — it can never quietly widen its reach.
- Each installed plugin shows a card with its name, state, requested capabilities, recent **log** and any load errors, and buttons to **Enable / Disable**, **Uninstall**, or (if pending) **Approve…**.
- **Reload** re-scans the folder for newly added plugins; **Open Plugins Folder** opens it in your file manager.

### What plugins can add

- **Commands** — appear in the **Ctrl+K command palette**.
- **Menu items** — under a top-level **Plugins** menu.
- **Open and export formats** — extra file types in the Open dialog and the Export / Export Assets dialogs.
- **Keyboard shortcuts** — a plugin command can suggest a default shortcut (rebindable like any other).
- **Batch operations** — run from **Plugins ▸ Batch Process…**: queue a list of files and a plugin processes them all headlessly, with progress and cancel.

### Writing a plugin

A plugin is a .NET assembly plus a `manifest.json` declaring its capabilities. The complete authoring guide — quick start, manifest reference, every host API, lifecycle and security — is in **`docs/plugin/AUTHORING.md`**, with a working example in **`samples/Sable.SamplePlugin`**.

---

## Settings reference

**Edit ▸ Preferences** — searchable, grouped by category:

| Category | Settings |
|---|---|
| **General** | Language · reopen last session on startup · show welcome screen · limit initial zoom to 100% |
| **Document** | Default DPI for new documents |
| **User Interface** | Theme (Dark / Gray / Light) · accent colour · canvas overlay colours (guides, smart guides, grid, quick mask) · transparency checker size and colours · precise brush cursor · panel visibility |
| **Performance** | Undo limit per document (default 256) · autosave on/off and interval |
| **Colour** | Working colour info (linear RGBA float pipeline) |
| **Machine Learning** | AI on/off (licence walkthrough) · per-feature model rows with Install · Smart Select quality (Auto/Fast/Balanced/Thorough) · model folder · generative tier on/off · model sources |
| **Plugins** | Enable plugins · install from folder/.zip · approve / enable / disable / uninstall each plugin · per-plugin log · reload · open plugins folder (see [Plugins](#plugins)) |
| **Updates** | Automatic update checks |
| **Keyboard** | **Migration preset** — apply a Photoshop or Affinity keymap in one click. Rebind any command shortcut: click a row, press the combination (a modifier or F-key is required), Backspace unbinds, Reset restores the default. Conflicts are reassigned with a warning. Tool letters and navigation keys are fixed. |
| **About** | Version, runtime, GPU renderer, licence |

---

## Keyboard shortcuts

### Tools

Press to activate, press again to cycle the group, **hold for a temporary tool**.

| Key | Tools |
|---|---|
| V | Move / Transform |
| M | Rectangle ▸ Elliptical Marquee |
| L | Lasso ▸ Polygonal Lasso |
| W | Magic Wand ▸ Colour Range ▸ Smart Select |
| B | Brush ▸ Pencil ▸ Eraser |
| G | Fill ▸ Gradient |
| C | Crop |
| U | Rectangle ▸ Rounded Rect ▸ Ellipse ▸ Polygon ▸ Star ▸ Line ▸ Arrow |
| S | Clone ▸ Heal ▸ Spot Heal ▸ Patch |
| O | Dodge ▸ Burn ▸ Sponge ▸ Blur ▸ Sharpen ▸ Smudge |
| Y | Liquify ▸ Mesh Warp |
| T | Text |
| P | Pen ▸ Node |
| I | Eyedropper |
| H | Hand |
| Z | Zoom |

### Commands (all rebindable in Preferences ▸ Keyboard; apply a Photoshop/Affinity preset there to migrate)

| Shortcut | Command |
|---|---|
| Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S | New / Open / Save / Save As |
| Ctrl+W | Close tab |
| Ctrl+Z · Ctrl+Y · Ctrl+Shift+Z | Undo · Redo |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / Cut / Paste |
| Ctrl+Shift+C / Ctrl+Shift+V / Ctrl+Alt+V | Copy Merged / Paste Into / Paste in Place |
| Ctrl+J | Duplicate layer |
| Alt+Backspace / Ctrl+Backspace | Fill with foreground / background |
| Ctrl+A / Ctrl+D / Ctrl+Shift+I | Select All / Deselect / Invert selection |
| Ctrl+G / Ctrl+Shift+G | Group / Ungroup |
| Ctrl+E / Ctrl+Shift+E / Ctrl+Shift+Alt+E | Merge Down / Merge Visible / Stamp Visible |
| Ctrl+Plus / Ctrl+Minus / Ctrl+0 / Ctrl+1 | Zoom in / out / Fit / 100% |
| Ctrl+K | Command palette |

### Canvas and colours (fixed)

| Input | Action |
|---|---|
| Wheel | Zoom at cursor |
| Middle-drag · Space-drag | Pan |
| + / - / 0 | Zoom in / out / fit |
| Arrow keys | Pan |
| [ / ] | Brush size down / up |
| Ctrl+Alt + drag | Brush HUD (size horizontally, hardness vertically) |
| Alt+click | Sample colour (paint tools) · zoom out (Zoom tool) · set source (Clone/Heal) |
| X / D | Swap / reset foreground–background |
| K | Edit layer mask |
| Q | Quick mask |
| \\ (hold) | Compare — hide all adjustments and live filters |
| ` | Radial quick-tool menu |
| Enter / Esc | Commit / cancel (pen path, crop, mesh warp, text, dialogs) |
| Delete / Backspace | Delete selection contents |

**Help ▸ Keyboard Shortcuts** shows this list inside the application.

---

## Tips and good practice

- **Stay non-destructive.** Prefer adjustment layers, live filters, masks and layer FX over direct pixel edits — they remain editable forever and recompute on the GPU at no cost to you.
- **Nest** an adjustment or filter inside a layer when it should affect only that layer; keep it top-level to grade the whole image. Use a **pass-through group** when an adjustment inside a group should reach below it.
- Hold **\\** any time to sanity-check your grade against the unprocessed image.
- **Ctrl+K** is faster than any menu; **`** is faster than the tool strip.
- **Smart Select + Remove Object** is the two-click way to delete anything from a photo.
- Crop with **Delete pixels off** — you can change your mind later by enlarging the canvas.
- Save working files as `.sable`; export PNG/JPEG/WebP only for delivery.
