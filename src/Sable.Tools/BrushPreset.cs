using System.Text.Json;

namespace Sable.Tools;

/// <summary>A saved brush configuration (PLAN §16 brush presets). Sampled-tip brushes
/// (e.g. imported from .abr) embed the greyscale tip bitmap.</summary>
public sealed class BrushPreset
{
    public string Name { get; set; } = "Preset";
    public float Radius { get; set; } = 16f;
    public float Hardness { get; set; } = 0.5f;
    public float Flow { get; set; } = 1f;
    public float Alpha { get; set; } = 1f;
    public float Spacing { get; set; }
    public bool PressureSize { get; set; } = true;
    public bool PressureFlow { get; set; }
    public bool Pencil { get; set; }

    // dab shape + dynamics (improvement plan §2)
    public float Angle { get; set; }
    public float Roundness { get; set; } = 1f;
    public float SizeJitter { get; set; }
    public float FlowJitter { get; set; }
    public float ScatterJitter { get; set; }
    public float AngleJitter { get; set; }
    public int PaintBlend { get; set; }                  // BlendMode int contract

    /// <summary>Greyscale sampled tip (base64 in JSON), or null = computed round dab.</summary>
    public byte[]? Tip { get; set; }
    public int TipW { get; set; }
    public int TipH { get; set; }

    /// <summary>Capture the brush's current paint parameters.</summary>
    public static BrushPreset From(string name, BrushTool b) => new()
    {
        Name = name,
        Radius = b.Radius, Hardness = b.Hardness, Flow = b.Flow, Alpha = b.Alpha,
        Spacing = b.Spacing, PressureSize = b.PressureSize, PressureFlow = b.PressureFlow,
        Pencil = b.Pencil,
        Angle = b.Angle, Roundness = b.Roundness,
        SizeJitter = b.SizeJitter, FlowJitter = b.FlowJitter,
        ScatterJitter = b.ScatterJitter, AngleJitter = b.AngleJitter,
        PaintBlend = (int)b.PaintBlend,
        Tip = b.Tip, TipW = b.TipW, TipH = b.TipH,
    };

    /// <summary>Apply this preset to the brush.</summary>
    public void ApplyTo(BrushTool b)
    {
        b.Radius = Radius; b.Hardness = Hardness; b.Flow = Flow; b.Alpha = Alpha;
        b.Spacing = Spacing; b.PressureSize = PressureSize; b.PressureFlow = PressureFlow;
        b.Pencil = Pencil;
        b.Angle = Angle; b.Roundness = Roundness;
        b.SizeJitter = SizeJitter; b.FlowJitter = FlowJitter;
        b.ScatterJitter = ScatterJitter; b.AngleJitter = AngleJitter;
        b.PaintBlend = (Sable.Core.BlendMode)PaintBlend;
        b.Tip = Tip; b.TipW = TipW; b.TipH = TipH;
    }
}

/// <summary>JSON persistence for brush presets (one file in the app's config dir).</summary>
public static class BrushPresetStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sable", "brushes.json");

    public static List<BrushPreset> Load(string? path = null)
    {
        try
        {
            var p = path ?? DefaultPath;
            if (!File.Exists(p)) return new();
            return JsonSerializer.Deserialize<List<BrushPreset>>(File.ReadAllText(p)) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(List<BrushPreset> presets, string? path = null)
    {
        try
        {
            var p = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(presets, Opts));
        }
        catch { /* best-effort persistence */ }
    }
}
