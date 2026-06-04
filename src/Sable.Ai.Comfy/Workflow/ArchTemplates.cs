namespace Sable.Ai.Comfy.Workflow;

/// <summary>
/// Per-architecture knobs for the ComfyUI graph (PHASE8_AI_COMFY §2.3). Kept tiny + pure; expands as more of
/// the user's families (Flux2 / Qwen-Image / SD3 / video) get real templates. For now it maps a Sable family
/// to the CLIP loader "type" ComfyUI expects for an assembled (standalone-transformer) pipeline.
/// </summary>
public static class ArchTemplates
{
    /// <summary>The <c>type</c> a CLIPLoader/DualCLIPLoader needs for this arch (ComfyUI's enum values).</summary>
    public static string ClipType(string? family) => (family ?? "").ToLowerInvariant() switch
    {
        "flux" => "flux",
        "sd3" => "sd3",
        "qwen" => "qwen_image",
        "hidream" => "hidream",
        "sdxl" => "sdxl",
        _ => "stable_diffusion",
    };

    /// <summary>True when this family is a still-image diffusion model Sable currently builds graphs for (vs
    /// video/audio archs that need bespoke graphs — surfaced as "unsupported" until templated).</summary>
    public static bool IsImageArch(string? family) => (family ?? "").ToLowerInvariant() switch
    {
        "ltx" or "wan" or "hunyuan" => false,   // video — not an image txt2img/inpaint graph
        _ => true,
    };
}
