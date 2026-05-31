# Phase 8 — AI subsystem (detailed plan)

Companion to [PLAN.md](PLAN.md) §6 / §16.15 / §18. This is the thorough build plan for Sable's  
local AI: smart selection, background removal, upscale, object removal (light tier, ships in app,  
no Python) and generative fill / expand / text-to-image (generative tier, opt-in Diffusers sidecar).

Read [PLAN.md](PLAN.md) §6 first — this document does not re-argue the locked decisions, it turns  
them into an executable, sliceable plan with seams, verification, and sequencing.

---

## 0\. Locked decisions (from PLAN.md — do not relitigate here)

*   **Two tiers.** Light = ONNX Runtime in-process, ships with the app, **no Python ever**. Generative  
    \= HuggingFace **Diffusers** sidecar (Apache-2.0), separate process, **opt-in install**. **No ComfyUI.**
*   **GPU-only, no CPU fallback.** Every AI op is hard-gated by VRAM fit; if it won't fit, the op is  
    **blocked with a clear message**, never silently degraded to CPU. The editor still runs fully  
    without an AI-capable GPU — AI menu items disable and explain why.
*   **User-provided weights only.** No bundled model catalog. Import from file/folder, or a URL the  
    user chooses. License of the weights is the user's responsibility.
*   **License: MIT.** Diffusers/torch are user-installed at runtime, not redistributed by us. Keep GPL out.
*   **Non-destructive + undoable.** Every AI result enters the document through the existing graph:  
    a new layer, a mask, or a selection — produced by an `IUndoableCommand`. An AI op that bakes  
    pixels with no undo entry is a bug, same rule as effects.
*   **Modules:** `Sable.Ai` (orchestration + ONNX light tier), `Sable.Ai.Sidecar` (Diffusers provisioning
    *   IPC client). Both currently empty scaffolds (csproj only).

## 0.1 Current state

*   `Sable.Ai` and `Sable.Ai.Sidecar` exist in the solution as empty projects (net10.0), no source.
*   The engine has everything AI needs to deposit results: `PixelLayer` (+ tiled atlas storage, partial  
    upload), per-layer `Mask` (R-channel coverage), `Document.SetMaskSelection` / selection channel,  
    `AddLayerCommand` / `PaintRasterCommand` / `RasterStateCommand` for undoable writes, the GPU  
    compositor, and `GpuCompositor.CompositeToBytes` for reading a flattened image to feed a model.
*   No ONNX Runtime / torch / sidecar dependencies are referenced yet.

---

## 1\. Architecture

### 1.1 The three model shapes (the seam that keeps features model-agnostic)

All AI features reduce to three input/output shapes. Define these in `Sable.Core` (engine-agnostic,  
no GPU/ONNX deps) so the engine and UI depend only on the shapes, never on a runtime:

```
// Sable.Core/Ai (pure contracts + DTOs, no ORT/torch)
record AiImage(byte[] Rgba, int Width, int Height);     // straight-alpha RGBA8, the universal payload
record AiMask(byte[] Coverage, int Width, int Height);   // single-channel 0..255
enum  AiPromptKind { Point, Box, Scribble }
record AiPrompt(AiPromptKind Kind, float X0, float Y0, float X1, float Y1, bool Positive);

interface IMaskModel       { Task<AiMask>  SegmentAsync(AiImage img, IReadOnlyList<AiPrompt> prompts, CancellationToken ct); }  // SAM2, BiRefNet, RMBG
interface IRasterModel     { Task<AiImage> ApplyAsync(AiImage img, AiMask? mask, AiParams p, CancellationToken ct); }          // ESRGAN (upscale/denoise), LaMa (inpaint)
interface IGenerativeModel { Task<AiImage> GenerateAsync(GenRequest req, CancellationToken ct); }                              // diffusion txt2img / inpaint / outpaint
```

*   `IMaskModel` covers selection + matting (prompted or whole-image).
*   `IRasterModel` covers image→image transforms (upscale, denoise, non-generative repair).
*   `IGenerativeModel` covers diffusion (prompt + optional image + optional mask → image).
*   A feature (e.g. "Remove Background") binds to a _shape_, not a concrete model. The model manager  
    decides which concrete adapter (and which weights) serves that shape for this op.

### 1.2 Backends

```
// Sable.Ai (references Sable.Core, ONNX Runtime, Sable.Imaging)
interface IAiBackend {
    string Name { get; }                        // "ONNX (DirectML)", "Diffusers sidecar"
    bool   IsAvailable { get; }                 // EP present / sidecar installed + healthy
    AiTier Tier { get; }                        // Light | Generative
    Task<ulong> ProbeFreeVramAsync();           // bytes free on the chosen device
}
```

*   **Light backend** (`OnnxBackend`): wraps ONNX Runtime `InferenceSession` per loaded model, with a  
    selected execution provider (see §1.5). Runs in-process. Adapters (`Sam2Adapter`,  
    `BiRefNetAdapter`, `EsrganAdapter`, `LamaAdapter`) implement `IMaskModel`/`IRasterModel` over a  
    session + pre/post-processing.
*   **Generative backend** (`SidecarBackend` in `Sable.Ai.Sidecar`): an IPC client to the Diffusers  
    process; implements `IGenerativeModel`. Never imports model code — pure protocol.

### 1.3 Orchestration (`Sable.Ai.AiService`)

The single entry point the App/UI calls. Holds the model registry, the active backends, the VRAM  
budget, and turns a "run feature X" request into: pick model → pre-flight VRAM gate → read pixels  
from the document (or selection region) → run the adapter → turn the result into an undoable command.

```
class AiService {
    ModelRegistry Registry { get; }
    IVramBudget   Vram { get; }
    Task<AiMask>  SelectSubjectAsync(Document doc, IReadOnlyList<AiPrompt> prompts, ...);   // -> selection
    Task          RemoveBackgroundAsync(Document doc, Layer target, ...);                   // -> mask command
    Task          UpscaleAsync(Document doc, Layer target, int factor, ...);                // -> new layer command
    Task          RemoveObjectAsync(Document doc, Layer target, AiMask mask, ...);          // -> raster command
    Task          GenerativeFillAsync(Document doc, AiMask mask, string prompt, ...);       // -> new layer (sidecar)
    // ... each returns / enqueues an IUndoableCommand; never mutates pixels directly
}
```

The App layer (MainWindow / a new `AiViewModel`) calls `AiService`, shows progress/cancel, and routes  
the produced command onto the active tab's `UndoStack` — exactly like every other edit.

### 1.4 How results enter the document (non-destructive, undoable)

| Feature | Result shape | Command | Notes |
| --- | --- | --- | --- |
| Smart selection (SAM2) | `AiMask` | sets the **selection** channel (`Document.SetMaskSelection`), undoable via the existing selection snapshot | live preview while dragging the prompt |
| Background removal | `AiMask` | `AddMaskCommand` on the target layer (R = subject coverage) | mask, not destructive erase |
| Upscale | `AiImage` (larger) | `AddLayerCommand` of a new `PixelLayer`, or resize-doc variant | tiled; see §3 |
| Object removal (LaMa) | `AiImage` (same size, masked region filled) | `RasterStateCommand` on the target layer (before/after buffer) | non-generative |
| Generative fill | `AiImage` (masked region) | `AddLayerCommand` of a new `PixelLayer` clipped to the mask | sidecar, opt-in |
| Generative expand | `AiImage` | doc-resize + `AddLayerCommand` of the new border content | sidecar, opt-in |
| Text-to-image | `AiImage` | `AddLayerCommand` of a new generated layer | sidecar, opt-in |

Reading the input image for an op uses `GpuCompositor.CompositeToBytes` (flattened doc) or a single  
layer's `Pixels`/region, depending on "sample: active layer vs all layers" (mirror the eyedropper option).

### 1.5 Execution-provider matrix (light tier)

ONNX Runtime selects a GPU execution provider per platform. Default to the broadest vendor-agnostic  
option, allow override in settings:

| OS | Default EP | Alternatives | Package |
| --- | --- | --- | --- |
| Windows | **DirectML** (any DX12 GPU) | CUDA/TensorRT (NVIDIA) | `Microsoft.ML.OnnxRuntime.DirectML` |
| Linux | **CUDA** (NVIDIA) | ROCm (AMD), DirectML via Vulkan n/a | `Microsoft.ML.OnnxRuntime.Gpu` |
| macOS | **CoreML** | CPU (blocked per policy for AI) | `Microsoft.ML.OnnxRuntime` + CoreML EP |

*   DirectML is the pragmatic Windows default (matches "one API for all GPUs", like wgpu choice).
*   EP availability is part of `IAiBackend.IsAvailable`. If no GPU EP is available, light AI is disabled  
    with an explanatory message (no CPU fallback, per policy).
*   Native ORT + EP binaries are per-RID; packaging picks them up per platform (Phase 9 cross-platform).

### 1.6 GPU sharing / VRAM budget (`IVramBudget`)

Editor (wgpu) and AI (ORT/sidecar) share the GPU. Plan:

*   `GpuProbe` reports total/free VRAM: DXGI/`IDXGIAdapter3::QueryVideoMemoryInfo` (Windows), NVML  
    (NVIDIA), or the EP's own query. A coarse free-VRAM number is enough for gating.
*   Pre-flight gate: `required = peak-resident component set (+ working set estimate)`. For diffusion
    this is **NOT the naive sum of all components** — see component offload below. If `required > free`,
    **block** with a message naming the model and the shortfall (suggest a smaller/quantized variant,
    a smaller text encoder, or enabling offload).
*   **"GPU-only, no CPU fallback" = GPU-only COMPUTE, not GPU-only weight staging.** Staging an idle
    component's weights in system RAM between stages is allowed and is how big models fit:
    *   **Sequential / component offload** (Diffusers `enable_model_cpu_offload`): encode the prompt
        with the text encoder, move it to RAM, THEN load the denoiser to VRAM and sample, THEN the VAE
        decodes. Peak VRAM ≈ max(single component) + latents, not the sum. The T5-XXL (~9GB) need not
        be resident while the denoiser runs. The sidecar exposes an `offload` mode the gate accounts for.
    *   This does NOT violate the policy: every component still *computes* on the GPU; only idle weights
        live in RAM. (Full CPU *inference* remains forbidden.)
*   Editor cooperation: during a heavy AI op, the compositor can shrink the tile-atlas budget and evict  
    non-visible tiles (the residency layer from Phase 7 already supports eviction — wire a  
    `TileResidency` budget setter). Restore after.
*   UI: a GPU/VRAM meter in the AI panel + the status bar; per-op offload toggle when a model is tight.

---

## 2\. Module / namespace layout

```
Sable.Core/Ai/            # contracts + DTOs only: AiImage, AiMask, AiPrompt, AiParams, GenRequest,
                          #   ModelManifest, AiTier, AiTaskKind  (no ORT/torch — keeps Core pure + testable)
Sable.Ai/
  AiService.cs            # orchestration entry point
  Backends/OnnxBackend.cs # ORT session host + EP selection
  Adapters/               # Sam2Adapter, BiRefNetAdapter, EsrganAdapter, LamaAdapter (pre/post + session)
  Models/ModelRegistry.cs # manifest load/save, import, per-task default, VRAM-fit query
  Gpu/GpuProbe.cs         # VRAM probe (per-OS); VramBudget
  Tiling/TileInference.cs # overlap-tile + feather merge for big-image raster models
Sable.Ai.Sidecar/
  SidecarBackend.cs       # IGenerativeModel over IPC
  Provisioning/UvEnv.cs   # uv venv bootstrap, torch wheel selection, progress, repair/uninstall
  Ipc/SidecarClient.cs    # HTTP/named-pipe client; JSON control + binary image frames
  server/                 # the Python Diffusers server (shipped as source, run in the provisioned venv)
Sable.App/
  ViewModels/AiViewModel  # binds AI ops to UI; progress/cancel; routes commands to the UndoStack
  Views/ModelsPanel, AiSettingsPage, GenerativeInstallFlow
```

`Sable.Core/Ai` (pure) is the key boundary: the engine and the unit tests depend on it without pulling  
ONNX or Python. `Sable.Ai` adds the runtime. `Sable.Ai.Sidecar` adds the opt-in Python world.

---

## 3\. Tiled inference (large images)

Raster models (ESRGAN, LaMa) and segmentation on big docs cannot run a 100MP image in one pass.  
`TileInference` (pure, unit-testable):

*   Split the input into overlapping tiles (e.g. 512 with 32-px overlap; configurable per model).
*   Run each tile through the adapter (respect the VRAM gate per tile, not per whole image).
*   Feather-merge overlaps (linear ramp in the overlap band) to avoid seams.
*   For upscale, tile geometry scales by the factor; merge in the output resolution.
*   SAM2 differs: it encodes the whole image once at a fixed size (downscaled), then decodes prompts  
    cheaply — tiling is for the encoder input resolution, not output stitching.

The tiling math (split/overlap/feather weights) is pure and gets unit tests independent of any model.

---

## 4\. Model manager (manifests + registry)

*   **Manifest** (`model.json`, in `Sable.Core/Ai`): `name`, `kind` (`base` | `adapter` | `component`),
    `family` (SAM2/BiRefNet/ESRGAN/LaMa/SD1.5/SDXL/SD3/Flux/Qwen/…), `tasks` (segment / matte / upscale /
    inpaint / txt2img / outpaint), `vramBytes` (required), `inputSize`, `recommended params`,
    `adapter` (which code runs it), `tier`, and a **`components`** block (see below) instead of a flat
    file list.
*   **Diffusion pipelines are NOT monolithic — components matter.** A diffusion base = denoiser
    (UNet / DiT transformer) + one-or-more **text encoder(s)** (+ their tokenizers) + **VAE** +
    scheduler. The manifest's `components` map covers `{ denoiser, textEncoders[], vae, scheduler }`,
    each either an inline file path or a **reference to a `component` model** (by id). Loading resolves
    three real-world layouts:
    *   **Single-file checkpoint** (SD1.5 / SDXL `.safetensors`): bundles UNet+CLIP+VAE → Diffusers
        `from_single_file`; `components` is just `{ checkpoint: path }` and the encoders are implied.
    *   **Diffusers folder**: components in subfolders → `from_pretrained`.
    *   **Assembled**: denoiser + a SEPARATELY-installed text encoder / VAE. This is the SD3/Flux case —
        the **T5-XXL text encoder (~9GB) and CLIP are shipped standalone and shared** across many
        bases, so the manifest *references* a `component` model (`textEncoders: [t5xxl-id, clip-l-id]`)
        and the user installs the big encoder ONCE. The registry resolves the refs and refuses to load
        a base whose required encoder/VAE component isn't installed (clear "missing component: T5-XXL"
        message + import prompt).
*   **Compatibility is per-component**, not just per-family: a base declares which text-encoder /
    VAE families it accepts (SD1.5 = CLIP-L; SDXL = CLIP-L + CLIP-bigG; SD3 = CLIP-L + CLIP-bigG +
    T5-XXL; Flux = CLIP-L + T5-XXL). The registry validates the resolved component set before a run.
*   **Adapter models — LoRA / ControlNet / IP-Adapter** (`kind: adapter`): these attach ON TOP of a
    base diffusion model rather than running standalone. Extra manifest fields: `adapterType`
    (`lora` | `controlnet` | `ip-adapter`), `appliesTo` (compatible base families, e.g. `[SDXL]` —
    a SD1.5 LoRA must not load onto Flux), `defaultWeight` (LoRA scale, 0..~1.5), optional
    `triggerWords`. A base model + zero-or-more compatible adapters form a **stack**; the registry
    validates compatibility and rejects mismatched pairings.
*   **Registry** (`Sable.Ai`): load all manifests from the user's model folder, import a new model or
    adapter (auto-draft a manifest via filename/format heuristics — `.safetensors` LoRA detection,
    user-editable), set a per-task default base, query "does this fit the detected GPU?".
*   **UI** (`ModelsPanel`): list installed base models + adapters (grouped), import button, VRAM-fit
    badge vs detected GPU, per-task default picker. Every generative op's dialog has a **base-model
    dropdown + a LoRA stack** (add/remove compatible LoRAs, per-LoRA weight slider) + param presets.
*   **Acquisition — import OR download** (PLAN §6.3 allows "download a direct/HF URL the user chooses";
    we never bundle/redistribute weights):
    *   **Download by URL** — paste a direct or HuggingFace URL (or `repo-id + file`); the downloader
        (`Sable.Ai/Download/ModelDownloader`) streams it into `models/<id>/`, with progress, resume
        (HTTP range), and optional SHA-256 verify, then auto-drafts + writes `model.json`.
    *   **Curated "recommended" list** — a small BUILT-IN set of POINTERS (`Sable.Core/Ai/RecommendedModels`):
        name · family · task · HF/direct URL · download size · VRAM · **license** · input size. The user
        clicks one to download; **the app ships only the metadata + link, never the weights**, and shows
        each model's license before the download starts (the no-bundled-catalog rule = no bundled
        WEIGHTS, not no convenience pointers; license responsibility stays the user's).
*   Manifest parsing, adapter↔base compatibility, the recommended-catalog data, URL→filename/manifest
    drafting, and VRAM-fit logic are pure → unit-tested without weights. The download itself is network
    (manual/integration test).

---

## 5\. Generative sidecar (opt-in)

*   **Trigger:** Settings → "Install generative AI" (disabled features until then). Light AI works without it.
*   **Provisioning** (`UvEnv`): create an isolated venv with `uv` (or pinned `python-build-standalone`),  
    detect GPU vendor → install the matching accelerated torch wheel (CUDA/ROCm/DirectML/MPS) + Diffusers
    *   loaders. Progress UI, resumable, offline-cacheable. Version-lock + health-check + repair/uninstall.  
        Never touch system Python.
*   **Server** (`server/`): a thin Python process around Diffusers exposing a stable local API  
    (HTTP over localhost is the v1 choice — debuggable, language-agnostic; named pipe later if needed).  
    Endpoints: `health`, `vram`, `load_model`, `set_adapters`, `txt2img`, `inpaint`, `outpaint`,
    `cancel`. JSON control, PNG/raw binary image frames.
*   **`load_model` resolves the component set** the app sends (denoiser + text encoder(s) + tokenizer(s)
    + VAE + scheduler paths, already resolved from the registry incl. shared/referenced components):
    single-file → `from_single_file`; folder → `from_pretrained`; assembled → construct the pipeline
    from explicit component paths (e.g. Flux denoiser + standalone T5-XXL + CLIP + VAE). Honors an
    `offload` flag (`enable_model_cpu_offload` / sequential offload) so big text encoders don't sit in
    VRAM during sampling. Returns the actual peak VRAM used so the gate can self-correct. Missing a
    required component (e.g. T5-XXL not installed) → a structured error the app surfaces with an import
    prompt, never a crash.
*   **LoRA / adapters** (Diffusers `load_lora_weights` + `set_adapters` / `fuse_lora`): `GenRequest`
    carries the base model plus a `loras: [{ name, weight }]` list (and optional `controlnet` /
    `ipAdapter` + conditioning image). The sidecar applies the LoRA stack to the loaded base before
    sampling, scales each by its weight, and unloads/swaps between requests. LoRA VRAM cost adds to
    the base's `vramBytes` in the pre-flight gate. Multiple LoRAs stack; the registry guarantees they
    are all `appliesTo` the chosen base family.
*   **Lifecycle** (`SidecarBackend`): app starts/stops the process, health-checks, probes VRAM, surfaces  
    errors. The app never imports model code — clean, swappable boundary.
*   **Protocol** is defined as DTOs in `Sable.Core/Ai` (incl. `GenRequest.Loras` + adapter fields) and
    is unit-testable (serialize/deserialize + adapter-compat validation) with a mock server,
    independent of an actual Python install.

---

## 6\. Sub-phase breakdown (slices)

Each slice is independently shippable + verified, following the project's slice model. Light tier  
(8.0–8.5) is a hard milestone that ships **with no Python**. Generative (8.6+) is the opt-in tier.

### 8.0 — AI infra + seams (no models) — DONE

*   `Sable.Core/Ai` contracts + DTOs (AiImage/AiMask/AiPrompt/AiParams/AdapterRef/GenRequest, the three
    model interfaces + `IAiBackend`, `ModelManifest`/`ModelComponents`/`ComponentSource`, `ModelCatalog`
    with component resolution + adapter compat + `VramParts`, `VramGate` pure decision).
*   `Sable.Ai`: `AiService` skeleton (`CheckReadiness` → `AiReadiness`/`AiBlockReason`), `ModelRegistry`
    (JSON load/save, heuristic `DraftFromFile`, per-task defaults), `GpuProbe` stub (env-overridable;
    real DXGI/NVML probe deferred to the gating-polish slice).
*   App: **"AI" top-menu** (Remove Background / Select Subject / Upscale / Remove Object / Generative
    Fill / Models) — each runs the pre-flight readiness check and explains why it's unavailable
    (no model / no GPU / missing component / won't fit VRAM) via `ConfirmWindow`.
*   **No ONNX dependency yet** — pure C# + a stub backend.
*   **Verified:** 18 unit tests (VRAM gate sum-vs-peak/offload, catalog resolution incl. missing shared
    T5-XXL, adapter↔base compat, manifest JSON round-trip, `DraftFromFile` heuristics, registry
    save/load + default, `AiService` readiness for every block reason) + 208 total + app launch (AI menu present).

### 8.1 — ONNX Runtime + Background Removal (BiRefNet / RMBG) — DONE

*   `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 added to `Sable.Ai` (Windows/vendor-agnostic GPU EP).
*   `Sable.Ai/Backends/OnnxBackend` (`IAiBackend`): DirectML session host (sequential exec + no mem
    pattern per DML reqs), session cache per model path, `IsAvailable` = `DmlExecutionProvider` present,
    **no CPU fallback** (throws if DML absent), `CreateMaskModel(manifest)` factory.
*   `Sable.Ai/Adapters/BiRefNetAdapter` (`IMaskModel`): resize → ImageNet-normalize CHW → run →
    sigmoid-or-direct mask → resize back. Auto logits-vs-prob detection. Pre/post math in
    `Sable.Ai/Imaging/ImageOps` (pure: bilinear resize RGBA/gray, `ToChwFloat` with BGR option,
    `MaskFromFloat`, `CoverageToRgbaMask`).
*   `Sable.Engine/Commands/SetMaskCommand` (undoable whole-mask swap). `AiService.RemoveBackgroundAsync`
    → segment the selected layer → returns the command; App "AI ▸ Remove Background" runs it on the
    selected `PixelLayer` and pushes it on the tab's undo stack (mask = alpha matte; reversible).
*   **Verified:** ImageOps unit tests (resize identity/constant, CHW normalize + BGR swap, sigmoid mask,
    coverage→RGBA) + `OnnxBackend` construction smoke (ORT native loads + EP enumeration, no weights)
    + 215 total + app launch. **Inference itself is NOT headless-verifiable** (no bundled weights) —
    the manual check: drop a BiRefNet ONNX + `model.json` (`adapter: "matte"`, `tasks: ["Matte"]`,
    `files: [...]`) into `%AppData%/Sable/models/<id>/`, select a layer, AI ▸ Remove Background.

### 8.2 — Upscale (Real-ESRGAN) + tiled inference — DONE

*   `Sable.Ai/Tiling/TileInference`: pure `Plan` (overlap tiles covering the image) + `Weight` (feather,
    floored so borders normalise to a single tile) + `Accumulate`/`Finalize` (weighted merge) + async
    `RunAsync(model, src, …, progress, ct)` — crops each tile → `model.ApplyAsync` → merges, **scale
    factor inferred from the first tile's output** (x2/x4/1:1), per-tile progress.
*   `EsrganAdapter` (`IRasterModel`, RGB 0..1 → factor× single-pass) + `ImageOps.Crop`/`ChwFloatToRgba`
    + `OnnxBackend.CreateRasterModel`. `AiService.UpscaleAsync` → tiled → new `PixelLayer` above the
    source (`AddLayerCommand`, undoable). App "AI ▸ Upscale" runs it in the modal `BusyWindow`, driving
    `BusyWindow.Progress` per tile (real 0–100%) + Cancel between tiles.
*   **DONE — verified:** TileInference + ImageOps tests (228 total) + app launch. Inference NOT
    headless-verifiable (no weights) — manual: install a Real-ESRGAN ONNX (`adapter:"esrgan"`,
    `tasks:["Upscale"]`). Catalog still BiRefNet-only (ESRGAN pointer needs a verified URL; paste any
    URL). Doc-resize-to-fit = follow-up (upscaled layer extends past the canvas).
*   _(original plan:)_ "Image/Layer → Upscale" → new `PixelLayer` (or doc resize). Tiled so large images don't OOM.
*   **Verify:** tiling unit tests (split/overlap/feather sum-to-one, seam-free merge of a synthetic  
    gradient); smoke harness with a user model.

### 8.3 — Smart selection (SAM2) — ONE-CLICK DONE; interactive tool = follow-up

*   **DONE**: `Sam2Adapter` (`IMaskModel`): image **encoder** (Files[0], embedding cached by content
    hash) + **decoder** (Files[1], per-prompt) → mask → `Document.SetMaskSelection`. `Sam2Ops` (pure):
    doc-px prompts → model-space `point_coords`/`point_labels` (SAM labels 1/0 point, 2/3 box corners) +
    centre-point default. `OnnxBackend` `sam2` branch (2 files). `AiService.SelectSubjectAsync`. App
    **AI ▸ Select Subject** = centre-point one-click → selection, in the modal `BusyWindow`. Decoder
    inputs wired by NAME (image_embed/high_res_feats/point_coords/point_labels/mask_input/
    has_mask_input/orig_im_size) — **best-effort; SAM2 export I/O varies, may need a tweak per export**.
*   **Verified:** `Sam2Ops` prompt-geometry tests (centre point, point/box scaling to model space,
    negative label) + 232 + app launch. Inference NOT headless-verifiable; SAM2 needs an
    encoder+decoder ONNX pair (`adapter:"sam2"`, `Files:[encoder,decoder]`).
*   **8.3b — Affinity-style hover-to-select — DONE (pipeline; live-verify the visual)**: `Sam2Adapter.SegmentEverythingAsync`
    (automatic mask generation: encode once → decoder over an n×n seed grid → `ObjectMask`s at a bounded
    work res → `AmgOps.Nms` dedupe). `Sable.Core/Ai/AmgOps` (pure: `GridPoints`/`IoU`/`Nms`/`BestAt` —
    tested) + `ObjectMask`. `AiService.SegmentEverythingAsync(layer,grid=32)`. **`ToolKind.SmartSelect`**
    in the **W selection flyout** (toolbox, like Affinity) — entering it precomputes the **active layer's**
    objects (`OnToolChanged`→`StartSmartSelect`, BusyWindow, once per layer). Hover highlights the object
    under the cursor as **diagonal stripes — blue=replace / green=add(Shift) / red=subtract(Alt)** (blit
    shader binding 7 = preview R8 tex + `previewMode` uniform; `GpuSurfaceControl.SmartSelect.cs`
    `UpdateSmartHover`/`SmartSelectClick` reuse `CaptureSelMode`+`ApplyMask`). Click commits to the
    selection (per-active-layer). Verified: AmgOps tests + 238 total + 8-binding blit shader compiles +
    launch. Inference + exact visual = live (needs SAM2 weights). Follow-up: box-drag prompt, distinct
    flyout icon, options bar.
*   **Verify:** prompt-encoding + mask-postprocess unit tests; embedding-cache logic test; smoke harness.

### 8.4 — Object removal (LaMa)

*   `LamaAdapter` (`IRasterModel`, mask→inpaint, non-generative). "Paint mask → Remove" fast path,  
    no install. Tiled for large regions. → `RasterStateCommand` on the layer.
*   **Verify:** mask-dilation + tiling tests; smoke harness.

### 8.5 — Model manager UI + gating polish ← LIGHT TIER COMPLETE (ships, no Python)

*   `ModelsPanel`: installed list, import, VRAM-fit badges, per-task default, per-op override dropdowns.
*   **Model acquisition — DONE EARLY (after 8.1, user-asked)**: `Sable.Core/Ai/RecommendedModels`
    (curated pointer list — name/url/size/vram/licence, no bundled weights) + `Sable.Ai/Download/ModelDownloader`
    (recommended OR direct/HF `owner/repo/file` shorthand → stream to `models/<id>/`, progress, auto-draft
    manifest, register) + a minimal `ModelsWindow` (recommended rows + paste-URL box + installed list),
    opened from AI ▸ Models. Remaining for 8.5: VRAM-fit badges, per-task defaults, LoRA stacks, import.
*   VRAM meter + honest pre-flight gating UX across all light features; editor tile-eviction cooperation  
    during heavy ops (wire `TileResidency` budget).
*   **Gate / milestone:** light AI fully usable with user-provided ONNX weights, zero Python, GPU-gated.
*   **Verify:** registry/gating unit tests; full launch with the Models panel.

### 8.6 — Generative sidecar: provisioning + IPC (no features yet)

*   `UvEnv` provisioning (venv, torch wheel by vendor, progress/resume/repair/uninstall), `SidecarBackend`  
    lifecycle, `SidecarClient` IPC, the Python Diffusers `server/` with `health`/`vram`/`load_model`.
*   **Component resolution + offload**: registry resolves a base's referenced text-encoder / VAE
    components (shared T5-XXL/CLIP installed once), sends the resolved set to `load_model`; the
    missing-component path (import prompt) and the `offload` mode are exercised here, before any
    generative feature uses them.
*   Settings → "Install generative AI" flow (opt-in, progress, disk/VRAM honesty).
*   **Verify:** IPC protocol unit tests against a mock server; provisioning is integration-tested on a  
    dev machine with network (documented manual step — cannot run in CI headlessly).

### 8.7 — Generative fill / inpaint (sidecar) + LoRA stack

*   "Paint mask + prompt → Generative Fill": region + mask + prompt → `inpaint` → new clipped layer.
*   Per-op model picker (base diffusion model) + **LoRA stack UI** (add/remove compatible LoRAs,
    per-LoRA weight slider) + seed/steps/cfg presets. Registry enforces LoRA↔base compatibility;
    pre-flight VRAM gate includes the LoRA cost.
*   **Verify:** request-building + adapter-compat unit tests (LoRA rejected on incompatible base,
    weights serialize); manual end-to-end with an installed base + LoRA.

### 8.8 — Generative expand / outpaint + text-to-image

*   Outpaint: extend canvas → fill the new border. Txt2img: new generated layer from a prompt.
*   **Verify:** geometry/request tests; manual end-to-end.

### 8.9 — Contention + polish

*   VRAM budget manager hardening (editor/AI co-tenancy), cancellation everywhere, error surfaces,  
    GPU/VRAM meter, batching/queue for multiple AI ops, docs.

---

## 7\. Verification strategy (the headless constraint)

AI verification is the hard part: we cannot bundle weights, and real inference needs a GPU + (for  
generative) a multi-GB Python env + network. Plan accordingly so the agent loop stays verifiable:

*   **Pure logic is the bulk of the testable surface** and gets real unit tests with no model: manifest  
    parse, **component resolution** (shared text-encoder/VAE refs resolve; missing-component detected),
    **per-component compatibility** (SDXL needs dual CLIP; Flux needs CLIP-L + T5-XXL; reject wrong
    encoder), adapter↔base compat (LoRA family), VRAM-fit gating with/without offload (peak-resident
    vs naive sum), registry import/default, tiling split/overlap/feather math, pre/post-processing
    (resize/normalize/argmax/threshold) against synthetic tensors, IPC DTO round-trips, command
    construction (does "remove bg" build the right `AddMaskCommand`?).
*   **A model smoke harness** (dev-only, e.g. `Sable.Ai.Smoke` or a spike mode) runs a real adapter  
    **iff** a user-supplied weights file exists at a known path; otherwise it **skips** cleanly. This is  
    how a developer/user validates actual inference; it is not part of the headless CI gate.
*   **App launch** stays the integration canary (no crash, menus reflect availability).
*   **Sidecar** provisioning + generation are **documented manual integration steps** (network + GPU);  
    the IPC layer itself is unit-tested against a mock server.
*   Never claim an AI feature "works" from a build+launch alone — distinguish "pipeline wired + pure  
    logic tested" from "inference verified with weights" in every status report.

---

## 8\. Dependencies & licensing

| Dependency | Where | License | Notes |
| --- | --- | --- | --- |
| ONNX Runtime (+ DirectML/CUDA/CoreML EP) | `Sable.Ai`, bundled per-RID | MIT | light tier, ships in app |
| `uv` / python-build-standalone | provisioned at runtime | Apache-2.0 / PSF | not redistributed; fetched on opt-in |
| Diffusers + torch | user venv (opt-in) | Apache-2.0 / BSD | not redistributed; user-installed |
| Model weights (SAM2/BiRefNet/ESRGAN/LaMa/SD/Flux/…) | user-provided | model-specific | user's responsibility, never bundled |

App stays MIT: nothing GPL, nothing copyleft redistributed. The generative stack is fetched into a  
user-owned venv on explicit opt-in, not shipped.

---

## 9\. Risks & open questions (resolved in context)

*   **GPU contention (editor vs AI).** Mitigation: VRAM budget manager + tile eviction (reuse Phase 7  
    residency) + honest pre-flight gating. Known constraint, not fully solved — design for "block, don't  
    crash".
*   **Heavy generative footprint + multi-component models.** Flux/SD3 ship a huge T5-XXL text encoder
    (~9GB) plus CLIP + VAE as separate pieces. Mitigation: shared `component` models (install the big
    encoder once, many bases reference it), **sequential component offload** (idle encoder → RAM, peak
    VRAM ≈ max single component, not the sum), quantized variants, honest gating. Light tier serves
    everyone without a big GPU. No CPU *compute* fallback by design (offload stages weights, still
    computes on GPU).
*   **Sidecar weight (GB-scale Python).** Mitigation: opt-in only; base app ships native ONNX, stays lean.
*   **Cross-platform EP packaging.** Per-RID native ORT + EP binaries; ties into Phase 9. Windows/DirectML  
    first; Linux/CUDA + macOS/CoreML follow with the platform backends.
*   **Min hardware floor:** light tier needs any DirectML/CUDA/CoreML-capable GPU (most modern GPUs);  
    generative needs 8GB+ (SDXL) to much more (Flux). Editor runs with no AI GPU at all.
*   **Resolved open questions** (PLAN.md §13): sidecar backend = **Diffusers** (no ComfyUI); weights =  
    **user-provided**; compute = **GPU-only, no CPU fallback**. These are locked, not open.

---

## 10\. Sequencing & gates

```
8.0 infra/seams  ──►  8.1 BgRemoval(ORT)  ──►  8.2 Upscale+tiling  ──►  8.3 SAM2  ──►  8.4 LaMa  ──►  8.5 Model mgr UI
                                                                                                         │
                                                                                         ── GATE: LIGHT TIER SHIPS (no Python) ──
                                                                                                         │
8.6 sidecar provision+IPC  ──►  8.7 gen fill  ──►  8.8 outpaint + txt2img  ──►  8.9 contention/polish
```

*   **8.0 first** — it is pure C# + tests, de-risks the seams, and unblocks every later slice.
*   **8.1 before 8.3** — background removal (no prompts) proves the ORT path with the simplest model  
    shape before tackling SAM2's encoder/decoder + prompt encoding.
*   **Light tier (8.0–8.5) is the shippable milestone** — ship it before any Python touches the app.
*   **Generative (8.6+) is opt-in** and decoupled; provisioning/IPC (8.6) lands before any generative  
    feature so the boundary is proven with `health`/`vram` before `inpaint`.

Each slice: pure logic unit-tested in CI; real inference validated via the dev smoke harness with  
user weights; app launch as the integration canary; status reports distinguish "wired + logic-tested"  
from "inference-verified".