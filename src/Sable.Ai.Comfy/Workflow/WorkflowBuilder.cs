using System.Text.Json;
using Sable.Core.Ai;

namespace Sable.Ai.Comfy.Workflow;

/// <summary>How ComfyUI loads this model: a full single-file checkpoint vs a standalone diffusion transformer
/// that needs separate CLIP + VAE (the modern <c>diffusion_models/</c> case).</summary>
public enum ComfyModelKind { Checkpoint, Unet }

/// <summary>What the graph needs to reference a model in ComfyUI loaders. Names are RELATIVE to the model-type
/// folder (e.g. <c>sdxl_base.safetensors</c>), which is how ComfyUI lists them. The App resolves these from a
/// <see cref="ModelManifest"/> (+ the user's encoder/VAE pick for the Unet case).</summary>
public sealed record ComfyModelRef(
    string Family,
    ComfyModelKind Kind,
    string Name,                 // ckpt_name (Checkpoint) or unet_name (Unet)
    IReadOnlyList<string>? ClipNames = null,   // Unet: text encoder file name(s)
    string? VaeName = null,                     // Unet: vae file name
    string? Weight = null);                     // unet weight_dtype hint (e.g. "fp8_e4m3fn"), optional

/// <summary>One resolved LoRA for a Comfy graph: the file name (relative to <c>loras/</c>) + strength.</summary>
public sealed record ComfyLora(string Name, double Strength);

/// <summary>
/// Builds a ComfyUI **API-format** workflow graph (PHASE8_AI_COMFY §2.3) from a <see cref="GenRequest"/> + a
/// resolved <see cref="ComfyModelRef"/>. The graph is the flat <c>{ "&lt;id&gt;": { class_type, inputs } }</c>
/// map ComfyUI's <c>/prompt</c> endpoint expects; node connections are <c>["&lt;id&gt;", outputIndex]</c>.
/// Pure JSON construction — fully unit-tested without running ComfyUI. Per-arch specifics live in
/// <see cref="ArchTemplates"/>; this covers the checkpoint txt2img / inpaint cases, the foundation the
/// assembled archs extend.
/// </summary>
public static class WorkflowBuilder
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

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

    /// <summary>Serialize a built graph dict to the JSON ComfyUI's <c>/prompt</c> wants.</summary>
    public static string ToJson(IReadOnlyDictionary<string, object> graph) => JsonSerializer.Serialize(graph, Json);

    /// <summary>A node: <c>{ class_type, inputs }</c>.</summary>
    private static Dictionary<string, object> Node(string classType, Dictionary<string, object> inputs)
        => new() { ["class_type"] = classType, ["inputs"] = inputs };

    /// <summary>A connection to another node's output: <c>["&lt;id&gt;", idx]</c>.</summary>
    private static object[] Link(string nodeId, int outIndex) => new object[] { nodeId, outIndex };

    private static IReadOnlyList<ComfyLora> Loras(GenRequest req, Func<string, string>? loraName)
    {
        if (req.Loras is null || loraName is null) return System.Array.Empty<ComfyLora>();
        var list = new List<ComfyLora>();
        foreach (var l in req.Loras)
        {
            var n = loraName(l.ModelId);
            if (!string.IsNullOrEmpty(n)) list.Add(new ComfyLora(n, l.Weight));
        }
        return list;
    }

    /// <summary>
    /// txt2img graph. <paramref name="loraName"/> maps a LoRA model-id → its <c>loras/</c> file name (the App
    /// supplies it from the registry). Returns the node map.
    /// </summary>
    public static Dictionary<string, object> Txt2Img(GenRequest req, ComfyModelRef model, int width, int height,
        Func<string, string>? loraName = null)
    {
        var g = new Dictionary<string, object>();
        var (modelOut, clipOut, vaeOut) = AddLoaders(g, model);
        (modelOut, clipOut) = AddLoras(g, Loras(req, loraName), modelOut, clipOut);

        g["pos"] = Node("CLIPTextEncode", new() { ["text"] = req.Prompt ?? "", ["clip"] = clipOut });
        g["neg"] = Node("CLIPTextEncode", new() { ["text"] = req.Negative ?? "", ["clip"] = clipOut });
        g["latent"] = Node("EmptyLatentImage", new() { ["width"] = width, ["height"] = height, ["batch_size"] = 1 });
        g["sampler"] = Sampler(req, modelOut, Link("pos", 0), Link("neg", 0), Link("latent", 0), denoise: 1.0);
        g["decode"] = Node("VAEDecode", new() { ["samples"] = Link("sampler", 0), ["vae"] = vaeOut });
        g["save"] = Node("SaveImage", new() { ["images"] = Link("decode", 0), ["filename_prefix"] = "sable" });
        return g;
    }

    /// <summary>
    /// Inpaint graph. <paramref name="imageName"/> is an uploaded RGBA image whose ALPHA is the inpaint mask
    /// (ComfyUI <c>LoadImage</c> yields IMAGE + MASK). <paramref name="denoise"/> controls edit strength.
    /// </summary>
    public static Dictionary<string, object> Inpaint(GenRequest req, ComfyModelRef model, string imageName,
        int width, int height, double denoise = 1.0, Func<string, string>? loraName = null)
    {
        var g = new Dictionary<string, object>();
        var (modelOut, clipOut, vaeOut) = AddLoaders(g, model);
        (modelOut, clipOut) = AddLoras(g, Loras(req, loraName), modelOut, clipOut);

        g["image"] = Node("LoadImage", new() { ["image"] = imageName });
        g["pos"] = Node("CLIPTextEncode", new() { ["text"] = req.Prompt ?? "", ["clip"] = clipOut });
        g["neg"] = Node("CLIPTextEncode", new() { ["text"] = req.Negative ?? "", ["clip"] = clipOut });
        g["encode"] = Node("VAEEncodeForInpaint", new()
        {
            ["pixels"] = Link("image", 0), ["vae"] = vaeOut, ["mask"] = Link("image", 1), ["grow_mask_by"] = 6,
        });
        g["sampler"] = Sampler(req, modelOut, Link("pos", 0), Link("neg", 0), Link("encode", 0), denoise);
        g["decode"] = Node("VAEDecode", new() { ["samples"] = Link("sampler", 0), ["vae"] = vaeOut });
        g["save"] = Node("SaveImage", new() { ["images"] = Link("decode", 0), ["filename_prefix"] = "sable" });
        return g;
    }

    /// <summary>Add the model loader(s); returns the MODEL / CLIP / VAE output links to chain from.</summary>
    private static (object[] model, object[] clip, object[] vae) AddLoaders(Dictionary<string, object> g, ComfyModelRef m)
    {
        if (m.Kind == ComfyModelKind.Checkpoint)
        {
            g["ckpt"] = Node("CheckpointLoaderSimple", new() { ["ckpt_name"] = m.Name });
            return (Link("ckpt", 0), Link("ckpt", 1), Link("ckpt", 2));
        }
        // standalone transformer: UNETLoader + CLIPLoader(s) + VAELoader (assembled; arch-specific clip type)
        g["unet"] = Node("UNETLoader", new() { ["unet_name"] = m.Name, ["weight_dtype"] = m.Weight ?? "default" });
        var clip = m.ClipNames ?? System.Array.Empty<string>();
        string clipType = ArchTemplates.ClipType(m.Family);
        if (clip.Count >= 2)
            g["clip"] = Node("DualCLIPLoader", new() { ["clip_name1"] = clip[0], ["clip_name2"] = clip[1], ["type"] = clipType });
        else
            g["clip"] = Node("CLIPLoader", new() { ["clip_name"] = clip.Count > 0 ? clip[0] : "", ["type"] = clipType });
        g["vae"] = Node("VAELoader", new() { ["vae_name"] = m.VaeName ?? "" });
        return (Link("unet", 0), Link("clip", 0), Link("vae", 0));
    }

    /// <summary>Chain LoRA loaders between the model/clip source and the rest; returns the new model/clip links.</summary>
    private static (object[] model, object[] clip) AddLoras(Dictionary<string, object> g, IReadOnlyList<ComfyLora> loras,
        object[] model, object[] clip)
    {
        for (int i = 0; i < loras.Count; i++)
        {
            var id = $"lora{i}";
            g[id] = Node("LoraLoader", new()
            {
                ["model"] = model, ["clip"] = clip, ["lora_name"] = loras[i].Name,
                ["strength_model"] = loras[i].Strength, ["strength_clip"] = loras[i].Strength,
            });
            model = Link(id, 0); clip = Link(id, 1);
        }
        return (model, clip);
    }

    private static Dictionary<string, object> Sampler(GenRequest req, object[] model, object[] pos, object[] neg, object[] latent, double denoise)
        => Node("KSampler", new()
        {
            ["model"] = model, ["positive"] = pos, ["negative"] = neg, ["latent_image"] = latent,
            ["seed"] = req.Seed < 0 ? System.Random.Shared.NextInt64(0, long.MaxValue) : req.Seed,
            ["steps"] = req.Steps, ["cfg"] = req.Cfg,
            ["sampler_name"] = "euler", ["scheduler"] = "normal", ["denoise"] = denoise,
        });
}
