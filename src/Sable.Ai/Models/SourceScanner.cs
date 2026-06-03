using System.Text.Json;
using System.Text.Json.Serialization;
using Sable.Core.Ai;

namespace Sable.Ai.Models;

/// <summary>
/// Walks an external model-source root on disk and turns it into drafted manifests via the pure
/// <see cref="ComfyLayout"/> rules (PHASE8_AI_SIDECAR §2.2/§2.3). For ComfyUI/Folder roots: enumerate weight
/// files under the recognised typed subfolders, draft each in place (NEVER copying, NEVER writing into the
/// tree), honour <c>extra_model_paths.yaml</c>, and refine each <c>.safetensors</c> base/adapter's
/// architecture with a header sniff. A per-source cache (keyed by path+size+mtime) avoids re-sniffing
/// unchanged files. The native (manifest-based) root is scanned by <see cref="ModelRegistry"/> itself.
/// </summary>
public static class SourceScanner
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One cached sniff result for a file (keyed by its absolute path).</summary>
    private sealed class CacheEntry { public long Size { get; set; } public long Mtime { get; set; } public string? Arch { get; set; } }

    private const int MaxFiles = 20_000;   // bounded scan so a giant tree can't hang the UI thread

    /// <summary>
    /// Normalise a user-picked folder to the actual models root: if it already holds recognised role
    /// subfolders use it; else if it has a <c>models/</c> child that does, use that (so picking the ComfyUI
    /// ROOT auto-detects its <c>models</c> folder, §2.2). Falls back to the picked path.
    /// </summary>
    public static string ResolveModelsRoot(string picked)
    {
        if (string.IsNullOrWhiteSpace(picked)) return picked;
        if (HasRoleSubfolder(picked)) return picked;
        var models = Path.Combine(picked, "models");
        if (Directory.Exists(models) && HasRoleSubfolder(models)) return models;
        return picked;
    }

    private static bool HasRoleSubfolder(string dir)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (ComfyLayout.RoleFor(Path.GetFileName(d)) is not null) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Scan one external source into drafted manifests. <paramref name="cacheDir"/> = where the
    /// per-source sniff cache lives (the native models folder's <c>.sources/</c>); null = no cache.</summary>
    public static IReadOnlyList<ModelManifest> Scan(ModelSource src, string? cacheDir = null)
    {
        var result = new List<ModelManifest>();
        if (src.Kind == ModelSourceKind.Native) return result;     // native = model.json, handled elsewhere
        if (string.IsNullOrWhiteSpace(src.Path) || !Directory.Exists(src.Path)) return result;

        var cache = LoadCache(cacheDir, src.Id);
        var nextCache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Emit(ModelManifest? m)
        {
            if (m is null || count >= MaxFiles) return;
            // de-dupe ids (a file reachable via both the standard tree and an extra-path entry)
            if (!seenIds.Add(m.Id)) return;
            result.Add(m);
            count++;
        }

        // 1) the standard typed-subfolder tree: <root>/<role>/.../<file>
        foreach (var abs in EnumerateWeights(src.Path))
        {
            if (count >= MaxFiles) break;
            var rel = Path.GetRelativePath(src.Path, abs);
            var arch = RefineArch(abs, cache, nextCache);
            Emit(ComfyLayout.DraftOne(src, rel, arch, FileSize(abs)));
        }

        // 2) extra_model_paths.yaml — role-tagged dirs that may live outside the tree
        foreach (var (role, dir) in ExtraRoots(src.Path))
        {
            if (count >= MaxFiles || !Directory.Exists(dir)) continue;
            foreach (var abs in EnumerateWeights(dir))
            {
                if (count >= MaxFiles) break;
                var relTail = Path.GetRelativePath(dir, abs).Replace('\\', '/');
                var arch = RefineArch(abs, cache, nextCache);
                Emit(ComfyLayout.DraftInRole(src, role, abs, $"{role}/{relTail}", arch, FileSize(abs)));
            }
        }

        SaveCache(cacheDir, src.Id, nextCache);
        return result;
    }

    private static long FileSize(string abs)
    {
        try { return new FileInfo(abs).Length; } catch { return 0; }
    }

    private static IEnumerable<string> EnumerateWeights(string root)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in files)
            if (ComfyLayout.IsWeightFile(f)) yield return f;
    }

    /// <summary>Sniff (or reuse a cached) architecture for a <c>.safetensors</c>; null for other formats.</summary>
    private static string? RefineArch(string abs, Dictionary<string, CacheEntry> cache, Dictionary<string, CacheEntry> next)
    {
        if (!abs.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) return null;
        long size = 0, mtime = 0;
        try { var fi = new FileInfo(abs); size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; } catch { }

        if (cache.TryGetValue(abs, out var hit) && hit.Size == size && hit.Mtime == mtime)
        {
            next[abs] = hit;
            return hit.Arch;
        }
        var arch = SafetensorsHeader.TryReadArch(abs);
        next[abs] = new CacheEntry { Size = size, Mtime = mtime, Arch = arch };
        return arch;
    }

    private static IEnumerable<(string Role, string Dir)> ExtraRoots(string modelsFolder)
    {
        // extra_model_paths.yaml usually sits at the ComfyUI root (the models folder's parent), sometimes beside it.
        foreach (var candidate in new[]
        {
            Path.Combine(modelsFolder, "extra_model_paths.yaml"),
            Path.Combine(Directory.GetParent(modelsFolder)?.FullName ?? modelsFolder, "extra_model_paths.yaml"),
        })
        {
            string text;
            try { if (!File.Exists(candidate)) continue; text = File.ReadAllText(candidate); }
            catch { continue; }
            foreach (var cfg in ComfyExtraPaths.Parse(text))
                foreach (var r in ComfyExtraPaths.ResolveRoots(cfg))
                    yield return (r.Role, r.AbsDir);
        }
    }

    private static string? CachePath(string? cacheDir, string sourceId)
        => cacheDir is null ? null : Path.Combine(cacheDir, ".sources", $"{Sanitize(sourceId)}.json");

    private static Dictionary<string, CacheEntry> LoadCache(string? cacheDir, string sourceId)
    {
        var p = CachePath(cacheDir, sourceId);
        try
        {
            if (p is not null && File.Exists(p))
                return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(p), Json)
                       ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveCache(string? cacheDir, string sourceId, Dictionary<string, CacheEntry> cache)
    {
        var p = CachePath(cacheDir, sourceId);
        if (p is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(cache, Json));
        }
        catch { }
    }

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var r = new string(chars).Trim('-');
        return r.Length == 0 ? "source" : r;
    }
}
