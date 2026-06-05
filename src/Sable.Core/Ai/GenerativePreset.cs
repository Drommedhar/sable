namespace Sable.Core.Ai;

/// <summary>
/// A user-configured generative model setup for one operation (PHASE8_AI_COMFY): a base model + its text
/// encoder(s) + VAE, named, pinned to a task. The Generative tab of the model manager builds these; the
/// Generative dialog then offers ONLY configured presets (so the user controls which models are usable —
/// video / incompatible archs simply aren't configured) and the user only picks LoRAs + the prompt. Pure
/// POCO (init props, JSON-friendly) stored in <c>SableSettings</c>.
/// </summary>
public sealed class GenerativePreset
{
    /// <summary>User-facing name (e.g. "Qwen-Image Fill").</summary>
    public string Name { get; set; } = "";

    /// <summary>The base model's registry id (a <c>checkpoints/</c> checkpoint or a <c>diffusion_models/</c> transformer).
    /// Used to inject the correct loader names into the workflow (overriding its baked, maybe wrong-OS, names).</summary>
    public string BaseModelId { get; set; } = "";

    /// <summary>For an assembled (standalone-transformer) base: the chosen text-encoder component ids. Empty for a checkpoint.</summary>
    public List<string> EncoderIds { get; set; } = new();

    /// <summary>For an assembled base: the chosen VAE component id. Null for a checkpoint.</summary>
    public string? VaeId { get; set; }

    /// <summary>The exported ComfyUI API-format workflow (.json) Sable runs for this preset — the user's own
    /// graph. Sable injects the image/prompt/seed/params + the base/encoder/VAE loader names above.</summary>
    public string? WorkflowFile { get; set; }

    /// <summary>True = text-to-image (no input image; output → a NEW document). False = image-fill/edit driven
    /// by the current selection. Determines the entry point + whether a selection is needed.</summary>
    public bool IsTextToImage { get; set; }
}
