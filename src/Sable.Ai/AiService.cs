using System.Threading;
using System.Threading.Tasks;
using Sable.Ai.Backends;
using Sable.Ai.Gpu;
using Sable.Ai.Imaging;
using Sable.Ai.Models;
using Sable.Core.Ai;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Commands;
using Sable.Engine.Layers;

namespace Sable.Ai;

/// <summary>Why an AI op can't run right now (drives the disabled-menu explanation, PHASE8_AI §1.3).</summary>
public enum AiBlockReason { None, NoGpu, NoModel, MissingComponent, WontFitVram }

/// <summary>Result of the pre-flight check for one AI task.</summary>
public sealed record AiReadiness(bool CanRun, AiBlockReason Reason, string Message, ModelManifest? Model);

/// <summary>
/// AI orchestration entry point (PHASE8_AI §1.3). Slice 8.0 wires the registry + GPU probe + the
/// pre-flight gating that every feature shares (model present? GPU present? components resolved?
/// fits VRAM?). The actual op methods (segment / matte / upscale / inpaint) arrive with their
/// backends in 8.1+, each producing an <c>IUndoableCommand</c> — this class never mutates pixels.
/// </summary>
public sealed class AiService
{
    public ModelRegistry Registry { get; }
    public GpuProbe Gpu { get; }
    private readonly List<IAiBackend> _backends = new();

    /// <summary>The opt-in generative backend (Diffusers sidecar), injected by the App once started (§4).
    /// AiService depends only on the Core <see cref="IGenerativeBackend"/> seam, never on the sidecar project.</summary>
    public IGenerativeBackend? Generative { get; set; }
    private string? _loadedSig;   // signature of the currently-loaded base+offload+lora stack (skip reload)

    public AiService(ModelRegistry registry, GpuProbe? gpu = null)
    {
        Registry = registry;
        Gpu = gpu ?? new GpuProbe();
    }

    public void AddBackend(IAiBackend backend) => _backends.Add(backend);

    public IReadOnlyList<IAiBackend> Backends => _backends;

    public bool HasBackend(AiTier tier) => _backends.Any(b => b.Tier == tier && b.IsAvailable);

    /// <summary>
    /// Pre-flight a task: is there a model, a GPU, are its components installed, and does it fit VRAM?
    /// Returns a reason + message when blocked so the UI can disable the action and explain why.
    /// </summary>
    public AiReadiness CheckReadiness(AiTaskKind task, bool offload = false, long workingSetBytes = 0)
    {
        var model = Registry.DefaultFor(task);
        if (model is null)
            return new AiReadiness(false, AiBlockReason.NoModel,
                $"No model installed for {task}. Import one in the Models panel.", null);

        bool gpuOk = Gpu.HasGpu || _backends.Any(b => b.Tier == model.Tier && b.IsAvailable);
        if (!gpuOk)
            return new AiReadiness(false, AiBlockReason.NoGpu,
                "No AI-capable GPU detected. AI runs GPU-only (no CPU fallback).", model);

        var res = Registry.Catalog.ResolveComponents(model);
        if (!res.Ok)
        {
            var missing = res.MissingRefs.Concat(res.MissingEncoderFamilies);
            return new AiReadiness(false, AiBlockReason.MissingComponent,
                $"Missing component(s): {string.Join(", ", missing)}. Install them in the Models panel.", model);
        }

        ulong free = Gpu.FreeVramBytes();
        if (free > 0)
        {
            var gate = Registry.Catalog.Gate(model, free, offload, workingSetBytes);
            if (!gate.Fit)
                return new AiReadiness(false, AiBlockReason.WontFitVram, gate.Message, model);
        }

        return new AiReadiness(true, AiBlockReason.None, "Ready.", model);
    }

    /// <summary>
    /// Background removal (PHASE8_AI §8.1): segment the layer's pixels into an alpha matte and return
    /// an undoable command that attaches it as the layer's mask. The caller pushes the command onto
    /// the active undo stack. Throws <see cref="AiNotReadyException"/> when pre-flight fails — never
    /// mutates the layer itself.
    /// </summary>
    public async Task<IUndoableCommand> RemoveBackgroundAsync(PixelLayer target, CancellationToken ct = default)
    {
        var ready = CheckReadiness(AiTaskKind.Matte);
        if (!ready.CanRun || ready.Model is null) throw new AiNotReadyException(ready.Message);

        var backend = _backends.OfType<OnnxBackend>().FirstOrDefault(b => b.IsAvailable)
            ?? throw new AiNotReadyException("No ONNX backend available.");
        var model = backend.CreateMaskModel(ready.Model);

        var img = new AiImage((byte[])target.Pixels.Clone(), target.Width, target.Height);
        var mask = await model.SegmentAsync(img, System.Array.Empty<AiPrompt>(), ct).ConfigureAwait(false);
        var rgbaMask = ImageOps.CoverageToRgbaMask(mask.Coverage, mask.Width, mask.Height);
        return new SetMaskCommand(target, rgbaMask);
    }

    /// <summary>
    /// Smart selection (PHASE8_AI §8.3): segment the selected layer with SAM2 using the given prompts
    /// (or a centre point for one-click "Select Subject") and return the coverage mask. The caller sets
    /// it as the document selection (selection ops aren't on the undo stack here).
    /// </summary>
    public async Task<AiMask> SelectSubjectAsync(
        PixelLayer target, IReadOnlyList<AiPrompt>? prompts = null, CancellationToken ct = default)
    {
        var ready = CheckReadiness(AiTaskKind.Segment);
        if (!ready.CanRun || ready.Model is null) throw new AiNotReadyException(ready.Message);

        var backend = _backends.OfType<OnnxBackend>().FirstOrDefault(b => b.IsAvailable)
            ?? throw new AiNotReadyException("No ONNX backend available.");
        var model = backend.CreateMaskModel(ready.Model);

        var img = new AiImage((byte[])target.Pixels.Clone(), target.Width, target.Height);
        return await model.SegmentAsync(img, prompts ?? System.Array.Empty<AiPrompt>(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Object removal (PHASE8_AI §8.4): inpaint the masked region of the layer with LaMa and return the
    /// new RGBA8 (same size; only the masked region changes). The caller wraps it in an undoable raster
    /// command. <paramref name="mask"/> = the region to erase (selection coverage).
    /// </summary>
    public async Task<byte[]> RemoveObjectAsync(PixelLayer target, AiMask mask, CancellationToken ct = default)
    {
        var ready = CheckReadiness(AiTaskKind.Inpaint);
        if (!ready.CanRun || ready.Model is null) throw new AiNotReadyException(ready.Message);
        var backend = _backends.OfType<OnnxBackend>().FirstOrDefault(b => b.IsAvailable)
            ?? throw new AiNotReadyException("No ONNX backend available.");
        var model = backend.CreateRasterModel(ready.Model);

        var img = new AiImage((byte[])target.Pixels.Clone(), target.Width, target.Height);
        var outImg = await model.ApplyAsync(img, mask, new AiParams(), ct).ConfigureAwait(false);
        return outImg.Rgba;
    }

    /// <summary>
    /// Automatic mask generation for hover-to-select (PHASE8_AI §8.3b): precompute every object in the
    /// layer via SAM2. Returns the object masks (each at its own bounded working resolution). The tool
    /// keeps these and picks the object under the cursor on hover.
    /// </summary>
    public async Task<IReadOnlyList<ObjectMask>> SegmentEverythingAsync(
        PixelLayer target, int grid = 32, IProgress<double>? progress = null, CancellationToken ct = default,
        bool forceCpu = false, Action? onCpuFallback = null)
    {
        var ready = CheckReadiness(AiTaskKind.Segment);
        if (!ready.CanRun || ready.Model is null) throw new AiNotReadyException(ready.Message);
        var backend = _backends.OfType<OnnxBackend>().FirstOrDefault(b => b.IsAvailable)
            ?? throw new AiNotReadyException("No ONNX backend available.");
        if (backend.CreateMaskModel(ready.Model) is not Adapters.Sam2Adapter sam)
            throw new AiNotReadyException("Smart-select needs a SAM2 model (encoder + decoder).");
        sam.ForceCpu = forceCpu;   // a prior run found this GPU can't run SAM2 → skip the GPU entirely

        var img = new AiImage((byte[])target.Pixels.Clone(), target.Width, target.Height);
        var result = await sam.SegmentEverythingAsync(img, grid, 384, progress, ct).ConfigureAwait(false);
        if (sam.FellBackToCpu && !forceCpu) onCpuFallback?.Invoke();   // GPU hung this run → persist CPU choice
        return result;
    }

    /// <summary>
    /// Upscale (PHASE8_AI §8.2): tile the selected layer through the ESRGAN model, feather-merge, and
    /// return a command that adds the result as a new layer above the source (the model's fixed scale,
    /// e.g. x4). The caller pushes the command on the undo stack. <paramref name="progress"/> reports
    /// 0..1 across tiles for the busy overlay.
    /// </summary>
    public async Task<IUndoableCommand> UpscaleAsync(
        Document doc, PixelLayer target, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var ready = CheckReadiness(AiTaskKind.Upscale);
        if (!ready.CanRun || ready.Model is null) throw new AiNotReadyException(ready.Message);

        var backend = _backends.OfType<OnnxBackend>().FirstOrDefault(b => b.IsAvailable)
            ?? throw new AiNotReadyException("No ONNX backend available.");
        var model = backend.CreateRasterModel(ready.Model);

        // match tile size to a fixed-shape model's input (e.g. 128) so no quality-killing resize; else 256.
        int tile = 256;
        if (model is Adapters.EsrganAdapter esr && esr.PreferredInputTile() is > 0 and int f) tile = f;

        var img = new AiImage((byte[])target.Pixels.Clone(), target.Width, target.Height);
        var up = await Tiling.TileInference.RunAsync(
            model, img, new AiParams(), tile: tile, overlap: System.Math.Max(8, tile / 8),
            progress: progress, ct: ct).ConfigureAwait(false);

        var layer = new PixelLayer(up.Width, up.Height, target.Name + " (upscaled)")
        { OffsetX = target.OffsetX, OffsetY = target.OffsetY };
        layer.SetBuffer(up.Width, up.Height, up.Rgba);

        var parent = doc.FindParent(target) ?? doc.Layers;
        int idx = parent.IndexOf(target) + 1;
        return new AddLayerCommand(doc, parent, layer, idx);
    }

    /// <summary>
    /// Ensure the generative sidecar has the right base (+ LoRA stack) loaded (PHASE8_AI_SIDECAR §3.5/§4).
    /// Builds a <see cref="LoadPlan"/> from the registry, blocks with a clear message on a missing component,
    /// and skips the reload when the same stack is already resident. Returns the resolved base manifest.
    /// </summary>
    public async Task<ModelManifest> EnsureModelLoadedAsync(
        string baseModelId, bool offload, IReadOnlyList<AdapterRef>? loras, CancellationToken ct = default)
    {
        if (Generative is null || !Generative.IsAvailable)
            throw new AiNotReadyException("Generative sidecar is not running. Enable it in Settings.");

        var baseModel = Registry.Catalog.ById(baseModelId) ?? Registry.DefaultFor(AiTaskKind.Inpaint)
            ?? throw new AiNotReadyException($"No generative base model '{baseModelId}'.");

        var plan = LoadPlan.Resolve(Registry.Catalog, baseModel, offload, loras);
        if (!plan.Ok || plan.Request is null)
            throw new AiNotReadyException(plan.Missing.Count > 0
                ? $"Missing component(s): {string.Join(", ", plan.Missing)}. Install them in the Models panel."
                : (string.IsNullOrEmpty(plan.Error) ? "Could not plan the model load." : plan.Error));

        var sig = Signature(baseModel.Id, offload, loras);
        if (sig == _loadedSig) return baseModel;

        var res = await Generative.LoadModelAsync(plan.Request, ct).ConfigureAwait(false);
        if (!res.Ok) { _loadedSig = null; throw new AiNotReadyException(string.IsNullOrEmpty(res.Error) ? "Model load failed." : res.Error); }
        _loadedSig = sig;
        return baseModel;
    }

    /// <summary>
    /// Generative fill / inpaint (PHASE8_AI_SIDECAR §4): inpaint the masked region of the layer with the
    /// generative base + prompt in <paramref name="spec"/>, and return a command that adds the result as a
    /// NEW layer clipped to the mask, above the source (non-destructive, undoable). <paramref name="region"/>
    /// must match the target layer's dimensions (the caller resamples the doc selection to the layer).
    /// <paramref name="spec"/> carries prompt/negative/steps/cfg/seed/base/loras/offload — its Image/Mask/Task
    /// are set here. Throws <see cref="AiNotReadyException"/> on pre-flight failure (never mutates the layer).
    /// </summary>
    public async Task<IUndoableCommand> GenerativeFillAsync(
        Document doc, PixelLayer target, AiMask region, GenRequest spec,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var baseId = !string.IsNullOrEmpty(spec.BaseModelId) ? spec.BaseModelId
            : Registry.DefaultFor(AiTaskKind.Inpaint)?.Id
              ?? throw new AiNotReadyException("No model installed for Generative Fill.");

        var baseModel = await EnsureModelLoadedAsync(baseId, spec.Offload, spec.Loras, ct).ConfigureAwait(false);

        var img = new AiImage((byte[])target.Pixels.Clone(), target.Width, target.Height);
        var req = spec with { BaseModelId = baseModel.Id, Task = AiTaskKind.Inpaint, Image = img, Mask = region };

        var outImg = await Generative!.GenerateAsync(req, ct).ConfigureAwait(false);

        var layer = new PixelLayer(outImg.Width, outImg.Height, target.Name + " (fill)")
        { OffsetX = target.OffsetX, OffsetY = target.OffsetY };
        layer.SetBuffer(outImg.Width, outImg.Height, outImg.Rgba);

        // clip the generated layer to the fill region so only the masked area shows
        if (region.Width == outImg.Width && region.Height == outImg.Height)
        {
            layer.Mask = ImageOps.CoverageToRgbaMask(region.Coverage, outImg.Width, outImg.Height);
            layer.MaskDirty = true;
        }

        var parent = doc.FindParent(target) ?? doc.Layers;
        int idx = parent.IndexOf(target) + 1;
        return new AddLayerCommand(doc, parent, layer, idx);
    }

    private static string Signature(string baseId, bool offload, IReadOnlyList<AdapterRef>? loras)
    {
        var loraSig = loras is null ? "" : string.Join(",", loras.Select(l => $"{l.ModelId}:{l.Weight}"));
        return $"{baseId}|{offload}|{loraSig}";
    }
}

/// <summary>Thrown when an AI op is invoked but its pre-flight (model/GPU/VRAM) isn't satisfied.</summary>
public sealed class AiNotReadyException : System.Exception
{
    public AiNotReadyException(string message) : base(message) { }
}
