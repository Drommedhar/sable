namespace Sable.Core.Ai;

/// <summary>Where a model-source root comes from, which drives how it is scanned.</summary>
public enum ModelSourceKind
{
    /// <summary>Sable's own folder: subfolders each carry a <c>model.json</c> (writable, downloads land here).</summary>
    Native,
    /// <summary>A ComfyUI <c>models/</c> tree: raw weights in typed subfolders, no manifests (read-only).</summary>
    ComfyUI,
    /// <summary>An Automatic1111 / Forge models tree (same typed-subfolder idea, different names) — read-only.</summary>
    Automatic1111,
    /// <summary>A generic folder scanned with the ComfyUI typed-subfolder rules — read-only.</summary>
    Folder,
}

/// <summary>
/// One model-source root the registry scans (PHASE8_AI_SIDECAR §2.1). The catalog is the union of every
/// enabled source. The <see cref="ModelSourceKind.Native"/> root is the only writable one (drafts/downloads
/// live there); external roots are referenced in place, read-only, never written into. Pure data.
/// </summary>
public sealed record ModelSource(
    string Id,            // stable key for settings + the per-source manifest cache; also prefixes model ids
    string Path,          // root dir (e.g. %AppData%/Sable/models, or D:\...\ComfyUI\models)
    ModelSourceKind Kind,
    bool ReadOnly,        // external roots = true; the native root = false
    bool Enabled = true)
{
    /// <summary>The always-present, writable Sable root.</summary>
    public static ModelSource Native(string path) => new("native", path, ModelSourceKind.Native, ReadOnly: false);

    /// <summary>An external ComfyUI models tree (read-only, scanned with the typed-subfolder rules).</summary>
    public static ModelSource Comfy(string id, string path) => new(id, path, ModelSourceKind.ComfyUI, ReadOnly: true);
}
