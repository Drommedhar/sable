using System.IO;

namespace Sable.Core.Ai;

/// <summary>
/// Pure mapping of a ComfyUI (or generic typed-subfolder) tree to drafted <see cref="ModelManifest"/>s
/// (PHASE8_AI_SIDECAR §2.2). Given a source + the relative paths of its weight files, classify each by its
/// top-level subfolder (<c>checkpoints/ loras/ vae/ clip/ unet/ controlnet/ upscale_models/ …</c>) and draft
/// an in-memory manifest that REFERENCES the file in place. No IO here beyond the path strings — fully
/// unit-testable with synthetic relpaths. Arch/encoder guesses are filename-only; a header sniff
/// (<see cref="SafetensorsHeader"/>) can refine them in the scanner.
/// </summary>
public static class ComfyLayout
{
    /// <summary>What a top-level subfolder means: the model role + how it runs.</summary>
    public sealed record Role(
        ModelKind Kind,
        AiTier Tier,
        AdapterType AdapterType,
        IReadOnlyList<AiTaskKind> Tasks,
        string? ComponentFamily,   // for Component roles (encoder/vae/denoiser family hint)
        string? Adapter);          // adapter code id for light-tier runnables (e.g. "esrgan")

    private static readonly IReadOnlyList<AiTaskKind> CheckpointTasks =
        new[] { AiTaskKind.Txt2Img, AiTaskKind.Inpaint, AiTaskKind.Outpaint };
    private static readonly IReadOnlyList<AiTaskKind> NoTasks = System.Array.Empty<AiTaskKind>();

    /// <summary>Weight file extensions we classify; everything else (yaml/json/txt/png/…) is ignored.</summary>
    public static bool IsWeightFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".safetensors" or ".ckpt" or ".pth" or ".pt" or ".bin" or ".onnx" or ".gguf";
    }

    /// <summary>Classify a top-level subfolder name; null = skip this folder entirely.</summary>
    public static Role? RoleFor(string subfolder) => subfolder.ToLowerInvariant() switch
    {
        "checkpoints" or "checkpoints_xl" =>
            new Role(ModelKind.Base, AiTier.Generative, AdapterType.None, CheckpointTasks, null, null),

        "unet" or "diffusion_models" =>
            new Role(ModelKind.Component, AiTier.Generative, AdapterType.None, NoTasks, "DENOISER", null),

        "loras" or "lora" or "lycoris" =>
            new Role(ModelKind.Adapter, AiTier.Generative, AdapterType.Lora, NoTasks, null, null),

        "vae" =>
            new Role(ModelKind.Component, AiTier.Generative, AdapterType.None, NoTasks, "VAE", null),

        "clip" or "text_encoders" or "text_encoder" =>
            new Role(ModelKind.Component, AiTier.Generative, AdapterType.None, NoTasks, "CLIP", null),

        "clip_vision" =>
            new Role(ModelKind.Component, AiTier.Generative, AdapterType.None, NoTasks, "CLIP-Vision", null),

        "controlnet" or "controlnets" or "t2i_adapter" =>
            new Role(ModelKind.Adapter, AiTier.Generative, AdapterType.ControlNet, NoTasks, null, null),

        "ipadapter" or "ip-adapter" or "ipadapters" =>
            new Role(ModelKind.Adapter, AiTier.Generative, AdapterType.IpAdapter, NoTasks, null, null),

        "upscale_models" or "upscalers" or "esrgan" =>
            new Role(ModelKind.Base, AiTier.Generative, AdapterType.None, new[] { AiTaskKind.Upscale }, null, "esrgan"),

        // prompt-side / preview / unsupported-in-v1 folders → skip
        "vae_approx" or "embeddings" or "style_models" or "gligen" or "hypernetworks"
            or "configs" or "photomaker" or "diffusers" => null,

        _ => null,
    };

    /// <summary>Draft a manifest for every weight file whose top-level subfolder is a recognised role.</summary>
    public static IEnumerable<ModelManifest> Draft(ModelSource src, IReadOnlyList<string> relPaths)
    {
        foreach (var rel in relPaths)
        {
            var m = DraftOne(src, rel);
            if (m is not null) yield return m;
        }
    }

    /// <summary>
    /// Draft a single file under the standard layout (its first path segment is the role folder), or null
    /// if the folder/extension isn't classified. The on-disk path is <c>src.Path / relPath</c>.
    /// </summary>
    public static ModelManifest? DraftOne(ModelSource src, string relPath, string? archOverride = null, long sizeBytes = 0)
    {
        if (!IsWeightFile(relPath)) return null;
        var norm = relPath.Replace('\\', '/').TrimStart('/');
        int slash = norm.IndexOf('/');
        if (slash <= 0) return null;                       // a loose file at the root → no role
        var top = norm.Substring(0, slash);
        var role = RoleFor(top);
        if (role is null) return null;
        var abs = Path.Combine(src.Path, relPath);
        return Build(src, role, top, abs, $"{top}/{Path.GetFileNameWithoutExtension(norm)}", archOverride, sizeBytes);
    }

    /// <summary>
    /// Draft a file that lives directly inside a role-tagged directory (the <c>extra_model_paths.yaml</c>
    /// case, §2.3): the role is known from the yaml key, not from an enclosing folder. <paramref name="absFilePath"/>
    /// is the real on-disk path; <paramref name="idTail"/> disambiguates the generated id.
    /// </summary>
    public static ModelManifest? DraftInRole(ModelSource src, string roleSubfolder, string absFilePath, string idTail, string? archOverride = null, long sizeBytes = 0)
    {
        if (!IsWeightFile(absFilePath)) return null;
        var role = RoleFor(roleSubfolder);
        if (role is null) return null;
        return Build(src, role, roleSubfolder, absFilePath, idTail, archOverride, sizeBytes);
    }

    private static ModelManifest Build(ModelSource src, Role role, string top, string absFilePath, string idTail, string? archOverride = null, long sizeBytes = 0)
    {
        var nameNoExt = Path.GetFileNameWithoutExtension(absFilePath);
        var ext = Path.GetExtension(absFilePath).ToLowerInvariant();
        var id = $"{src.Id}:{idTail}";
        var arch = archOverride ?? GuessArch(nameNoExt);
        // VRAM estimate: weights load ~at their on-disk size (fp16). A single-file checkpoint bundles every
        // component, so its file size approximates the whole pipeline; assembled bases sum components via
        // ModelCatalog.VramParts at gate time. Better than 0 (which always "fits"); refined by load_model.
        long vram = sizeBytes > 0 ? sizeBytes : 0;

        // upscale .onnx can run in the in-proc light tier; .pth/.pt must go through the sidecar's torch.
        var tier = role.Tier;
        var adapter = role.Adapter;
        if (role.Tasks.Contains(AiTaskKind.Upscale))
        {
            if (ext == ".onnx") { tier = AiTier.Light; adapter = "esrgan"; }
            else { tier = AiTier.Generative; adapter = null; }
        }

        return role.Kind switch
        {
            ModelKind.Adapter => new ModelManifest
            {
                Id = id, Name = nameNoExt, SourceId = src.Id, Kind = ModelKind.Adapter,
                Family = arch ?? top, Tier = AiTier.Generative,
                AdapterType = role.AdapterType,
                AppliesTo = arch is null ? null : new[] { arch },
                DefaultWeight = 1.0, VramBytes = vram,
                Files = new[] { absFilePath },
            },

            ModelKind.Component => new ModelManifest
            {
                Id = id, Name = nameNoExt, SourceId = src.Id, Kind = ModelKind.Component,
                Family = role.ComponentFamily ?? "component", Tier = AiTier.Generative,
                ComponentFamily = ComponentFamilyFor(role.ComponentFamily, nameNoExt, arch),
                VramBytes = vram,
                Files = new[] { absFilePath },
            },

            _ => new ModelManifest   // Base
            {
                Id = id, Name = nameNoExt, SourceId = src.Id, Kind = ModelKind.Base,
                Family = role.Tasks.Contains(AiTaskKind.Upscale) ? (arch ?? "ESRGAN") : (arch ?? "unknown"),
                Tier = tier, Tasks = role.Tasks, Adapter = adapter, VramBytes = vram,
                AcceptsTextEncoders = role.Tasks.Contains(AiTaskKind.Upscale) ? null : EncodersFor(arch),
                Files = new[] { absFilePath },
            },
        };
    }

    /// <summary>Best-effort diffusion architecture from a filename (null = unknown → user sets it).</summary>
    public static string? GuessArch(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("flux")) return "Flux";
        if (n.Contains("sd3") || n.Contains("sd_3") || n.Contains("stable-diffusion-3")) return "SD3";
        if (n.Contains("sdxl") || n.Contains("xl-base") || n.Contains("_xl") || n.Contains("-xl")
            || n.Contains("pony") || n.Contains("illustrious") || n.Contains("playground")) return "SDXL";
        if (n.Contains("sd15") || n.Contains("sd_15") || n.Contains("v1-5") || n.Contains("v1.5")) return "SD1.5";
        if (n.Contains("sd21") || n.Contains("v2-1") || n.Contains("768-v")) return "SD2";
        return null;
    }

    /// <summary>Text-encoder families a base of this arch needs (drives missing-component checks).</summary>
    public static IReadOnlyList<string>? EncodersFor(string? arch) => arch switch
    {
        "SD1.5" or "SD2" => new[] { "CLIP-L" },
        "SDXL" => new[] { "CLIP-L", "CLIP-bigG" },
        "SD3" => new[] { "CLIP-L", "CLIP-bigG", "T5-XXL" },
        "Flux" => new[] { "CLIP-L", "T5-XXL" },
        _ => null,
    };

    /// <summary>Refine a component family from the filename (T5-XXL / CLIP-L / CLIP-bigG / VAE-&lt;arch&gt;).</summary>
    public static string ComponentFamilyFor(string? roleFamily, string name, string? arch)
    {
        var n = name.ToLowerInvariant();
        if (roleFamily == "CLIP" || roleFamily == "DENOISER")
        {
            if (n.Contains("t5xxl") || n.Contains("t5_xxl") || n.Contains("t5-xxl") || n.Contains("t5")) return "T5-XXL";
            if (n.Contains("clip_g") || n.Contains("clip-g") || n.Contains("bigg") || n.Contains("clip_bigg")) return "CLIP-bigG";
            if (n.Contains("clip_l") || n.Contains("clip-l")) return "CLIP-L";
            if (roleFamily == "DENOISER") return arch is null ? "DENOISER" : $"DENOISER-{arch}";
            return "CLIP-L";
        }
        if (roleFamily == "VAE") return arch is null ? "VAE" : $"VAE-{arch}";
        return roleFamily ?? "component";
    }
}
