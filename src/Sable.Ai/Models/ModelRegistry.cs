using System.Text.Json;
using System.Text.Json.Serialization;
using Sable.Core.Ai;

namespace Sable.Ai.Models;

/// <summary>
/// Filesystem-backed model registry (PHASE8_AI §4): scans a models folder for <c>model.json</c>
/// manifests into a <see cref="ModelCatalog"/>, imports new weights (heuristic draft manifest),
/// and persists per-task defaults. Pure rules (resolution / compat / VRAM) live in the catalog;
/// this layer is the IO around it.
/// </summary>
public sealed class ModelRegistry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ModelsFolder { get; }
    public ModelCatalog Catalog { get; private set; } = new();

    /// <summary>Per-task default base-model id (user-chosen).</summary>
    public Dictionary<AiTaskKind, string> Defaults { get; private set; } = new();

    /// <summary>Extra read-only roots (ComfyUI / folder) scanned alongside the native folder (§2.1).</summary>
    private readonly List<ModelSource> _externalSources = new();

    public ModelRegistry(string modelsFolder) => ModelsFolder = modelsFolder;

    /// <summary>The native (writable) root plus every registered external source.</summary>
    public IReadOnlyList<ModelSource> Sources =>
        new[] { ModelSource.Native(ModelsFolder) }.Concat(_externalSources).ToList();

    /// <summary>Register an external source (ComfyUI/folder) and re-scan. Ignores the native kind + duplicates.</summary>
    public void AddSource(ModelSource src)
    {
        if (src.Kind == ModelSourceKind.Native) return;
        if (_externalSources.Any(s => string.Equals(s.Id, src.Id, StringComparison.OrdinalIgnoreCase))) return;
        _externalSources.Add(src);
        Load();
    }

    /// <summary>Forget an external source (never deletes the external tree) and re-scan.</summary>
    public void RemoveSource(string sourceId)
    {
        _externalSources.RemoveAll(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        Load();
    }

    /// <summary>Replace the external-source set wholesale (e.g. from persisted settings). <paramref name="scan"/>
    /// false defers the (potentially slow) external scan — pair with a later <see cref="Load"/> off the UI thread.</summary>
    public void SetSources(IEnumerable<ModelSource> sources, bool scan = true)
    {
        _externalSources.Clear();
        _externalSources.AddRange(sources.Where(s => s.Kind != ModelSourceKind.Native));
        if (scan) Load(); else LoadNativeOnly();
    }

    /// <summary>Scan only the native folder's <c>model.json</c>s (fast — no external tree walk / header sniff).
    /// Use at startup so the UI thread never blocks on a large/remote ComfyUI tree; follow with <see cref="Load"/>
    /// on a background thread to fold in external sources.</summary>
    public void LoadNativeOnly() => Catalog = ScanNative();

    /// <summary>(Re)scan every source into one catalog: the native folder's <c>model.json</c>s plus each
    /// enabled external root's drafted (referenced-in-place) manifests. The external walk + header sniff can be
    /// slow on a big/remote tree — call off the UI thread.</summary>
    public void Load()
    {
        var cat = ScanNative();
        foreach (var src in _externalSources)
        {
            if (!src.Enabled) continue;
            foreach (var m in SourceScanner.Scan(src, ModelsFolder)) cat.Add(m);
        }
        Catalog = cat;
    }

    private ModelCatalog ScanNative()
    {
        var cat = new ModelCatalog();
        if (Directory.Exists(ModelsFolder))
        {
            foreach (var file in Directory.EnumerateFiles(ModelsFolder, "model.json", SearchOption.AllDirectories))
            {
                var m = ParseManifest(File.ReadAllText(file));
                if (m is not null) cat.Add(m);
            }
            LoadDefaults();
        }
        return cat;
    }

    public static ModelManifest? ParseManifest(string json)
    {
        try { return JsonSerializer.Deserialize<ModelManifest>(json, Json); }
        catch { return null; }
    }

    public static string SerializeManifest(ModelManifest m) => JsonSerializer.Serialize(m, Json);

    /// <summary>The folder a model with this id lives in: <c>{ModelsFolder}/{safe-id}/</c>.</summary>
    public string ModelDir(string id) => Path.Combine(ModelsFolder, SafeId(id));

    /// <summary>True if a model with this id is installed (present in the catalog).</summary>
    public bool IsInstalled(string id) => Catalog.ById(id) is not null;

    /// <summary>Uninstall EVERY model: delete the whole models folder + reset the catalog/defaults.</summary>
    public void RemoveAll()
    {
        try { if (Directory.Exists(ModelsFolder)) Directory.Delete(ModelsFolder, recursive: true); } catch { /* locked → leave */ }
        Defaults = new Dictionary<AiTaskKind, string>();
        Catalog = new ModelCatalog();
    }

    /// <summary>Uninstall a model: delete its folder (weights + manifest) and re-scan.</summary>
    public void Remove(string id)
    {
        var dir = ModelDir(id);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* locked → leave it */ }
        Defaults = Defaults.Where(kv => !string.Equals(kv.Value, id, StringComparison.OrdinalIgnoreCase))
                           .ToDictionary(kv => kv.Key, kv => kv.Value);
        SaveDefaults();
        Load();
    }

    /// <summary>Write a manifest to <c>{ModelsFolder}/{id}/model.json</c> and add it to the catalog.</summary>
    public void Save(ModelManifest m)
    {
        var dir = ModelDir(m.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model.json"), SerializeManifest(m));
        Catalog.Add(m);
    }

    /// <summary>
    /// Draft a manifest from a weights file by filename/extension heuristics (PHASE8_AI §4) — the user
    /// edits it afterwards. Detects LoRA safetensors and the common ONNX light-model families.
    /// </summary>
    public static ModelManifest DraftFromFile(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var lower = name.ToLowerInvariant();
        var id = SafeId(name);

        // LoRA: a small-ish safetensors with "lora" in the name
        if (ext == ".safetensors" && lower.Contains("lora"))
            return new ModelManifest
            {
                Id = id, Name = name, Kind = ModelKind.Adapter, AdapterType = AdapterType.Lora,
                Family = "lora", Tier = AiTier.Generative, AppliesTo = new[] { "SDXL" }, DefaultWeight = 1.0,
                Files = new[] { filePath },
            };

        // ONNX light-tier families by name keyword
        (string family, AiTaskKind[] tasks, string adapter)? light = lower switch
        {
            _ when lower.Contains("sam")      => ("SAM2",      new[] { AiTaskKind.Segment }, "sam2"),
            _ when lower.Contains("birefnet") || lower.Contains("rmbg") || lower.Contains("u2net")
                                              => ("BiRefNet",  new[] { AiTaskKind.Matte },   "matte"),
            _ when lower.Contains("esrgan") || lower.Contains("upscal")
                                              => ("ESRGAN",    new[] { AiTaskKind.Upscale }, "esrgan"),
            _ when lower.Contains("lama")     => ("LaMa",      new[] { AiTaskKind.Inpaint }, "lama"),
            _                                 => default,
        };
        if (light is { } l)
            return new ModelManifest
            {
                Id = id, Name = name, Kind = ModelKind.Base, Family = l.family, Tier = AiTier.Light,
                Tasks = l.tasks, Adapter = l.adapter, Files = new[] { filePath },
            };

        // unknown → a base stub the user fills in
        return new ModelManifest { Id = id, Name = name, Kind = ModelKind.Base, Family = "unknown", Files = new[] { filePath } };
    }

    public void SetDefault(AiTaskKind task, string modelId)
    {
        Defaults[task] = modelId;
        SaveDefaults();
    }

    /// <summary>The chosen default base for a task, or the first installed model that can do it.</summary>
    public ModelManifest? DefaultFor(AiTaskKind task)
    {
        if (Defaults.TryGetValue(task, out var id) && Catalog.ById(id) is { } m) return m;
        return Catalog.ForTask(task).FirstOrDefault();
    }

    private string DefaultsPath => Path.Combine(ModelsFolder, "defaults.json");

    private void LoadDefaults()
    {
        try
        {
            if (File.Exists(DefaultsPath))
                Defaults = JsonSerializer.Deserialize<Dictionary<AiTaskKind, string>>(File.ReadAllText(DefaultsPath), Json) ?? new();
        }
        catch { Defaults = new(); }
    }

    private void SaveDefaults()
    {
        try
        {
            Directory.CreateDirectory(ModelsFolder);
            File.WriteAllText(DefaultsPath, JsonSerializer.Serialize(Defaults, Json));
        }
        catch { /* best-effort */ }
    }

    private static string SafeId(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var id = new string(chars).Trim('-');
        return id.Length == 0 ? "model" : id;
    }
}
