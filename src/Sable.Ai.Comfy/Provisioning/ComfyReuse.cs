using System.IO;
using System.Text;

namespace Sable.Ai.Comfy.Provisioning;

/// <summary>
/// Pure helpers for reusing a user's ComfyUI assets from a Sable-provisioned ComfyUI (PHASE8_AI_COMFY §2.4):
/// the <c>extra_model_paths.yaml</c> that points at the user's models folder, and the custom_nodes link plan.
/// No IO here — strings + path pairs only — so both are unit-tested without a real install.
/// </summary>
public static class ComfyReuse
{
    /// <summary>Roles ComfyUI's extra_model_paths understands (subfolder == key for a standard models tree).</summary>
    private static readonly string[] Roles =
    {
        "checkpoints", "diffusion_models", "unet", "loras", "vae", "clip", "text_encoders",
        "clip_vision", "controlnet", "ipadapter", "upscale_models", "embeddings", "vae_approx", "style_models",
    };

    /// <summary>
    /// Build an <c>extra_model_paths.yaml</c> that maps every role to the user's models folder, so the
    /// provisioned ComfyUI sees all their weights in place (none copied). <paramref name="userModelsDir"/> =
    /// the <c>ModelSource</c> path (the user's <c>…/ComfyUI/models</c>).
    /// </summary>
    public static string BuildExtraModelPaths(string userModelsDir)
    {
        var basePath = userModelsDir.Replace('\\', '/').TrimEnd('/');
        var sb = new StringBuilder();
        sb.Append("sable_reuse:\n");
        sb.Append($"    base_path: {basePath}\n");
        foreach (var r in Roles) sb.Append($"    {r}: {r}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Plan symlink/junction pairs (src → dst) for each of the user's custom-node subfolders into the
    /// provisioned ComfyUI's <c>custom_nodes</c>. <paramref name="userNodeSubdirNames"/> = the immediate
    /// subdirectory names of the user's <c>custom_nodes</c>. Pure; the caller creates the links.
    /// </summary>
    public static IReadOnlyList<(string Src, string Dst)> PlanCustomNodeLinks(
        string userCustomNodesDir, IEnumerable<string> userNodeSubdirNames, string ownCustomNodesDir)
    {
        var list = new List<(string, string)>();
        foreach (var name in userNodeSubdirNames)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) continue;   // skip dotfolders / __pycache__-ish
            if (string.Equals(name, "__pycache__", System.StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((Path.Combine(userCustomNodesDir, name), Path.Combine(ownCustomNodesDir, name)));
        }
        return list;
    }
}
