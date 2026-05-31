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

    public ModelRegistry(string modelsFolder) => ModelsFolder = modelsFolder;

    /// <summary>(Re)scan the models folder: each immediate subfolder with a <c>model.json</c> is a model.</summary>
    public void Load()
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
        Catalog = cat;
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
