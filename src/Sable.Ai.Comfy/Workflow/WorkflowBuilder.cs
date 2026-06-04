namespace Sable.Ai.Comfy.Workflow;

/// <summary>How ComfyUI loads this model: a full single-file checkpoint vs a standalone diffusion transformer
/// that needs separate CLIP + VAE (the modern <c>diffusion_models/</c> case).</summary>
public enum ComfyModelKind { Checkpoint, Unet }

/// <summary>What the graph needs to reference a model in ComfyUI loaders. Names are RELATIVE to the model-type
/// folder (e.g. <c>sdxl_base.safetensors</c>), which is how ComfyUI lists them. The App resolves these from a
/// <c>ModelManifest</c> (+ the user's encoder/VAE pick for the Unet case) and injects them into the user's
/// exported workflow (see <see cref="WorkflowTemplate"/>).</summary>
public sealed record ComfyModelRef(
    string Family,
    ComfyModelKind Kind,
    string Name,                 // ckpt_name (Checkpoint) or unet_name (Unet)
    IReadOnlyList<string>? ClipNames = null,   // Unet: text encoder file name(s)
    string? VaeName = null,                     // Unet: vae file name
    string? Weight = null);                     // unet weight_dtype hint (e.g. "fp8_e4m3fn"), optional

/// <summary>
/// Model-file → ComfyUI loader name/kind helpers. The graph itself comes from the user's exported workflow
/// template now (<see cref="WorkflowTemplate"/>), so Sable no longer builds per-arch graphs here — it only
/// resolves the names ComfyUI's loaders expect, to inject into the template.
/// </summary>
public static class WorkflowBuilder
{
    /// <summary>Classify a model file by its path: a <c>checkpoints/</c> file is a full single-file checkpoint;
    /// anything else generative (<c>diffusion_models/</c>, <c>unet/</c>, …) is a standalone transformer that
    /// needs assembling. Scans path SEGMENTS (robust to nesting), so both the panel and the backend agree.</summary>
    public static ComfyModelKind KindForPath(string filePath)
    {
        foreach (var seg in filePath.Replace('\\', '/').Split('/'))
            if (seg.Equals("checkpoints", System.StringComparison.OrdinalIgnoreCase)
                || seg.Equals("checkpoints_xl", System.StringComparison.OrdinalIgnoreCase))
                return ComfyModelKind.Checkpoint;
        return ComfyModelKind.Unet;
    }

    private static readonly System.Collections.Generic.HashSet<string> RoleFolders = new(System.StringComparer.OrdinalIgnoreCase)
    { "checkpoints", "checkpoints_xl", "diffusion_models", "unet", "loras", "vae", "clip", "text_encoders",
      "clip_vision", "controlnet", "ipadapter", "upscale_models", "embeddings" };

    /// <summary>
    /// The name as ComfyUI's loaders list it: the path RELATIVE to the model-type folder, with the OS
    /// separator (ComfyUI nests, e.g. <c>Qwen\qwen_image.safetensors</c> under <c>diffusion_models/</c>).
    /// Sending just the filename fails validation ("not in list") for nested models.
    /// </summary>
    public static string ComfyName(string filePath)
    {
        var segs = filePath.Replace('\\', '/').Split('/');
        int idx = -1;
        for (int i = segs.Length - 1; i >= 0; i--) if (RoleFolders.Contains(segs[i])) { idx = i; break; }
        if (idx < 0 || idx >= segs.Length - 1) return System.IO.Path.GetFileName(filePath);
        return string.Join(System.IO.Path.DirectorySeparatorChar, segs[(idx + 1)..]);
    }
}
