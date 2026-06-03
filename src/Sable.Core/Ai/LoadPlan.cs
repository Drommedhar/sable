using System.IO;

namespace Sable.Core.Ai;

/// <summary>Outcome of planning a base load (PHASE8_AI_SIDECAR §3.5): a ready request, or the missing
/// components that block it (surfaced as an import prompt — reuses the <c>MissingComponent</c> path).</summary>
public sealed record LoadPlanResult(
    bool Ok,
    LoadModelRequest? Request,
    IReadOnlyList<string> Missing,
    string Error = "");

/// <summary>
/// Pure planner that turns a base <see cref="ModelManifest"/> + the <see cref="ModelCatalog"/> into a
/// <see cref="LoadModelRequest"/> with every component path resolved (PHASE8_AI_SIDECAR §3.5). Handles the
/// three real layouts: single-file checkpoint (ComfyUI <c>checkpoints/*.safetensors</c> → <c>from_single_file</c>),
/// a Diffusers folder (<c>from_pretrained</c>), and an assembled pipeline (denoiser + standalone text-encoder(s)
/// + VAE, possibly shared <c>component</c> models). Reuses <see cref="ModelCatalog.ResolveComponents"/> for ref
/// resolution + missing-component detection. No IO, no torch — fully unit-tested.
/// </summary>
public static class LoadPlan
{
    public static LoadPlanResult Resolve(ModelCatalog catalog, ModelManifest baseModel, bool offload = false,
        IReadOnlyList<AdapterRef>? loras = null)
    {
        if (baseModel.Kind != ModelKind.Base)
            return new LoadPlanResult(false, null, System.Array.Empty<string>(), "not a base model");

        var c = baseModel.Components;

        // --- assembled (explicit denoiser/encoders/vae, may reference shared components) ---
        if (c is not null && !c.IsBundled && (c.Denoiser is not null || (c.TextEncoders?.Count > 0) || c.Vae is not null))
        {
            var res = catalog.ResolveComponents(baseModel);
            if (!res.Ok)
            {
                var missing = res.MissingRefs.Concat(res.MissingEncoderFamilies).ToList();
                return new LoadPlanResult(false, null, missing, $"missing component(s): {string.Join(", ", missing)}");
            }

            string? denoiser = PathOf(catalog, c.Denoiser);
            string? vae = PathOf(catalog, c.Vae);
            var encoders = (c.TextEncoders ?? System.Array.Empty<ComponentSource>())
                .Select(te => PathOf(catalog, te)).Where(p => p is not null).Select(p => p!).ToList();

            var paths = new ComponentPaths(Denoiser: denoiser, TextEncoders: encoders, Vae: vae, Scheduler: c.Scheduler);
            return Ready(catalog, baseModel, PipelineKind.Assembled, paths, offload, loras);
        }

        // --- single-file checkpoint (bundled components) ---
        if (c is { IsBundled: true } && c.Checkpoint?.Path is { } ckPath)
            return Ready(catalog, baseModel, PipelineKind.SingleFile, new ComponentPaths(Checkpoint: ckPath), offload, loras);

        // --- no components block: infer from the file (the common ComfyUI checkpoints/ case) ---
        var file = baseModel.Files?.FirstOrDefault();
        if (string.IsNullOrEmpty(file))
            return new LoadPlanResult(false, null, System.Array.Empty<string>(), "base has no files");

        // a path with no weight extension is treated as a Diffusers folder (from_pretrained)
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
            return Ready(catalog, baseModel, PipelineKind.Pretrained, new ComponentPaths(PretrainedDir: file), offload, loras);

        return Ready(catalog, baseModel, PipelineKind.SingleFile, new ComponentPaths(Checkpoint: file), offload, loras);
    }

    private static LoadPlanResult Ready(ModelCatalog catalog, ModelManifest m, PipelineKind kind, ComponentPaths paths, bool offload, IReadOnlyList<AdapterRef>? loras)
        => new(true, new LoadModelRequest(m.Id, m.Family, kind, paths, offload, ResolveLoras(catalog, loras)), System.Array.Empty<string>());

    /// <summary>Resolve a LoRA stack (catalog ids → on-disk paths); unresolvable ids are dropped.</summary>
    private static IReadOnlyList<LoraSpec>? ResolveLoras(ModelCatalog catalog, IReadOnlyList<AdapterRef>? loras)
    {
        if (loras is null || loras.Count == 0) return null;
        var list = new List<LoraSpec>();
        foreach (var a in loras)
        {
            var path = catalog.ById(a.ModelId)?.Files?.FirstOrDefault();
            if (path is not null) list.Add(new LoraSpec(path, a.Weight, a.ModelId));
        }
        return list.Count > 0 ? list : null;
    }

    private static string? PathOf(ModelCatalog catalog, ComponentSource? cs)
    {
        if (cs is null || !cs.IsValid) return null;
        if (cs.IsPath) return cs.Path;
        return catalog.ById(cs.Ref!)?.Files?.FirstOrDefault();
    }
}
