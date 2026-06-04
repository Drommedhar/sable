using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sable.Ai.Comfy.Workflow;

/// <summary>
/// Drives a user's OWN ComfyUI workflow (exported as "Save (API Format)") instead of a hand-built graph
/// (PHASE8_AI_COMFY). The user's working .json is the source of truth for any architecture; Sable just
/// injects the run-time values by tracing the graph from the sampler:
///   • our uploaded image  → every <c>LoadImage</c> node
///   • the prompt / negative → the text node feeding the sampler's positive / negative conditioning
///   • seed / steps / cfg / denoise → the sampler
/// Pure JSON manipulation (System.Text.Json.Nodes) → unit-tested with a synthetic API graph.
/// </summary>
public static class WorkflowTemplate
{
    /// <summary>Model-passthrough optimization nodes that are bypassed by default — they only speed things up
    /// and often need optional deps Sable's env lacks (e.g. <c>sageattention</c>, torch.compile). Editable.</summary>
    public static readonly HashSet<string> BypassNodeTypes = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "PatchSageAttentionKJ", "PathchSageAttentionKJ", "PatchSageAttention",   // kjnodes ships the typo'd name
        "TorchCompileModel", "TorchCompileModelFluxAdvanced", "TorchCompileModelQwenImage", "TorchCompileVAE",
    };


    /// <summary>What to push into the loaded workflow for one run. <paramref name="Loras"/> = the user's chosen
    /// LoRAs (ComfyUI name + strength); the workflow's LoRA-loader nodes get these in order, and any leftover
    /// loader is bypassed (so a baked/stale LoRA that isn't installed can't fail validation).</summary>
    public sealed record Inject(string Prompt, string Negative, string ImageName, long Seed, int Steps, double Cfg, double Denoise,
        IReadOnlyList<(string Name, double Strength)>? Loras = null,
        // the preset's chosen models override the workflow's baked names (which may be stale / wrong-OS path)
        string? UnetName = null, string? CheckpointName = null, IReadOnlyList<string>? ClipNames = null, string? VaeName = null);

    /// <summary>Load an API-format workflow JSON + apply the injections; return the modified graph object for /prompt.</summary>
    public static JsonObject Build(string apiJson, Inject inj)
    {
        var root = JsonNode.Parse(apiJson)?.AsObject() ?? throw new System.FormatException("not a JSON object");
        ApplyTo(root, inj);
        return root;
    }

    /// <summary>Same as <see cref="Build"/> but returns JSON text (for tests).</summary>
    public static string Apply(string apiJson, Inject inj) => Build(apiJson, inj).ToJsonString();

    /// <summary>Mutate a parsed API graph in place (exposed for tests).</summary>
    public static void ApplyTo(JsonObject root, Inject inj)
    {
        // 1) every LoadImage → our uploaded image
        foreach (var (_, node) in Nodes(root))
            if (ClassType(node) == "LoadImage" && node!["inputs"] is JsonObject li && li.ContainsKey("image"))
                li["image"] = inj.ImageName;

        // 2) scalar params on whatever node carries them (architecture-agnostic: KSampler, RandomNoise's
        //    noise_seed, etc.) — only WIDGET inputs (linked inputs left alone). seed randomised when -1.
        long seed = inj.Seed < 0 ? System.Random.Shared.NextInt64(0, long.MaxValue) : inj.Seed;
        foreach (var (_, node) in Nodes(root))
        {
            if (node!["inputs"] is not JsonObject inp) continue;
            SetIfPresent(inp, "seed", seed);
            SetIfPresent(inp, "noise_seed", seed);
            SetIfPresent(inp, "steps", inj.Steps);
            SetIfPresent(inp, "cfg", inj.Cfg);
            SetIfPresent(inp, "denoise", inj.Denoise);
        }

        // 3) prompts: find the text nodes and decide positive/negative by title, then by emptiness (the
        //    negative encoder is usually the empty one). Works for KSampler, SamplerCustomAdvanced/guider, etc.
        string? posId = null, negId = null, firstText = null;
        foreach (var (id, node) in Nodes(root))
        {
            if (node!["inputs"] is not JsonObject inp) continue;
            var key = inp.ContainsKey("prompt") && inp["prompt"] is JsonValue ? "prompt"
                    : inp.ContainsKey("text") && inp["text"] is JsonValue ? "text" : null;
            if (key is null) continue;
            var title = (node["_meta"]?["title"]?.GetValue<string>() ?? "").ToLowerInvariant();
            firstText ??= id;
            if (title.Contains("negative")) { negId ??= id; }
            else if (title.Contains("positive")) { posId = id; }
            else if (string.IsNullOrEmpty(inp[key]!.GetValue<string>())) { negId ??= id; }
            else { posId ??= id; }
        }
        posId ??= firstText;
        if (posId is not null) SetPrompt(root, posId, inj.Prompt);
        if (negId is not null) SetPrompt(root, negId, inj.Negative);

        // 3) model loaders: override the workflow's baked model names with the preset's chosen models, so the
        //    names match THIS ComfyUI's list (the export may carry a stale / different-OS path).
        foreach (var (_, node) in Nodes(root))
        {
            if (node!["inputs"] is not JsonObject ni) continue;
            switch (ClassType(node))
            {
                case "UNETLoader" when inj.UnetName is not null && ni.ContainsKey("unet_name"): ni["unet_name"] = inj.UnetName; break;
                case "CheckpointLoaderSimple" when inj.CheckpointName is not null && ni.ContainsKey("ckpt_name"): ni["ckpt_name"] = inj.CheckpointName; break;
                case "VAELoader" when inj.VaeName is not null && ni.ContainsKey("vae_name"): ni["vae_name"] = inj.VaeName; break;
                case "CLIPLoader" when inj.ClipNames is { Count: > 0 } && ni.ContainsKey("clip_name"): ni["clip_name"] = inj.ClipNames[0]; break;
                case "DualCLIPLoader" when inj.ClipNames is { Count: >= 2 }:
                    if (ni.ContainsKey("clip_name1")) ni["clip_name1"] = inj.ClipNames[0];
                    if (ni.ContainsKey("clip_name2")) ni["clip_name2"] = inj.ClipNames[1];
                    break;
            }
        }

        // 3b) bypass optional optimization patch nodes that need deps Sable's env may lack (sage attention,
        //     torch.compile, …) — they're model passthroughs, so rewire the model around them.
        foreach (var (id, node) in Nodes(root).ToList())
            if (BypassNodeTypes.Contains(ClassType(node) ?? "")) Bypass(root, id);

        // 4) LoRA loaders: assign the user's chosen LoRAs in order; bypass any leftover loader so a baked /
        //    not-installed LoRA can't fail validation ("Value not in list").
        var loraIds = Nodes(root).Where(n => (ClassType(n.Node) ?? "").Contains("Lora", System.StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id).ToList();
        var chosen = inj.Loras ?? System.Array.Empty<(string, double)>();
        for (int i = 0; i < loraIds.Count; i++)
        {
            if (i < chosen.Count) SetLora(root, loraIds[i], chosen[i].Name, chosen[i].Strength);
            else Bypass(root, loraIds[i]);
        }
    }

    // --- helpers ---

    private static IEnumerable<(string Id, JsonNode? Node)> Nodes(JsonObject root)
    {
        foreach (var kv in root)
            if (kv.Value is JsonObject o && o.ContainsKey("class_type"))
                yield return (kv.Key, kv.Value);
    }

    private static string? ClassType(JsonNode? node) => node?["class_type"]?.GetValue<string>();

    private static void SetIfPresent(JsonObject inputs, string key, JsonNode value)
    {
        if (inputs.ContainsKey(key) && inputs[key] is not JsonArray) inputs[key] = value;   // don't overwrite a linked input
    }

    /// <summary>Best-effort steps + cfg/guidance defaults from a workflow, to pre-fill the dialog. Scans for a
    /// direct <c>steps</c>/<c>cfg</c>/<c>guidance</c> widget, or a PrimitiveInt titled "Steps". 0 = unknown.</summary>
    public static (int Steps, double Cfg) ReadDefaults(string apiJson)
    {
        int steps = 0; double cfg = 0;
        try
        {
            var root = JsonNode.Parse(apiJson)?.AsObject();
            if (root is null) return (0, 0);
            foreach (var (_, node) in Nodes(root))
            {
                if (node!["inputs"] is not JsonObject inp) continue;
                if (steps == 0 && inp["steps"] is JsonValue sv && sv.TryGetValue<int>(out var s)) steps = s;
                if (cfg == 0 && inp["cfg"] is JsonValue cv && cv.TryGetValue<double>(out var c)) cfg = c;
                if (cfg == 0 && inp["guidance"] is JsonValue gv && gv.TryGetValue<double>(out var g)) cfg = g;
                // flux2 etc.: steps live in a PrimitiveInt titled "Steps"
                if (steps == 0 && ClassType(node) == "PrimitiveInt"
                    && (node["_meta"]?["title"]?.GetValue<string>() ?? "").Contains("step", System.StringComparison.OrdinalIgnoreCase)
                    && inp["value"] is JsonValue pv && pv.TryGetValue<int>(out var ps)) steps = ps;
            }
        }
        catch { }
        return (steps, cfg);
    }

    private static void SetPrompt(JsonObject root, string nodeId, string text)
    {
        if (root[nodeId] is not JsonObject node || node["inputs"] is not JsonObject inp) return;
        if (inp.ContainsKey("prompt") && inp["prompt"] is JsonValue) inp["prompt"] = text;
        else if (inp.ContainsKey("text") && inp["text"] is JsonValue) inp["text"] = text;
    }

    private static void SetLora(JsonObject root, string nodeId, string name, double strength)
    {
        if (root[nodeId] is not JsonObject node || node["inputs"] is not JsonObject inp) return;
        if (inp.ContainsKey("lora_name")) inp["lora_name"] = name;
        if (inp.ContainsKey("strength_model")) inp["strength_model"] = strength;
        if (inp.ContainsKey("strength_clip")) inp["strength_clip"] = strength;
    }

    /// <summary>Remove a model(+clip) passthrough node (e.g. a LoRA loader) and reconnect its consumers to its
    /// model/clip source, so an unused loader drops out of the graph cleanly.</summary>
    private static void Bypass(JsonObject root, string nodeId)
    {
        if (root[nodeId] is not JsonObject node || node["inputs"] is not JsonObject inp) return;
        var modelSrc = inp.TryGetPropertyValue("model", out var m) && m is JsonArray ? m : null;
        var clipSrc = inp.TryGetPropertyValue("clip", out var c) && c is JsonArray ? c : null;
        if (modelSrc is null) return;   // not a model passthrough → leave it

        foreach (var (_, other) in Nodes(root))
        {
            if (other!["inputs"] is not JsonObject oi) continue;
            foreach (var key in oi.Select(kv => kv.Key).ToList())
            {
                if (oi[key] is not JsonArray a || a.Count < 2 || a[0]?.GetValue<string>() != nodeId) continue;
                int slot = a[1]?.GetValue<int>() ?? 0;
                oi[key] = (slot == 1 && clipSrc is not null ? clipSrc : modelSrc)!.DeepClone();
            }
        }
        root.Remove(nodeId);
    }
}
