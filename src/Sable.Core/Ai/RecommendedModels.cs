namespace Sable.Core.Ai;

/// <summary>One downloadable file of a model: a direct URL or "owner/repo/path" HF shorthand + the
/// local name to save it as. A model may have several (SAM2 = encoder + decoder).</summary>
public sealed record ModelFile(string Url, string FileName);

/// <summary>
/// A POINTER to a downloadable model (PHASE8_AI §4). The app ships only this metadata + URLs the user
/// may choose to download — never the weights, and the <see cref="License"/> is shown before any
/// download. Keeps the "no bundled WEIGHTS" rule while sparing a manual hunt. Pure data.
/// </summary>
public sealed record RecommendedModel(
    string Id,
    string Name,
    string Family,
    AiTier Tier,
    IReadOnlyList<AiTaskKind> Tasks,
    IReadOnlyList<ModelFile> Downloads,   // one file (most), or encoder+decoder (SAM2), in adapter order
    long SizeBytes,
    long VramBytes,
    int InputSize,
    string License,
    string Adapter,
    string LicenseUrl = "")   // raw URL of the original licence text (fetched + shown for per-model consent)
{
    /// <summary>Build the on-disk manifest once the files land at <paramref name="localPaths"/> (in Downloads order).</summary>
    public ModelManifest ToManifest(IReadOnlyList<string> localPaths) => new()
    {
        Id = Id, Name = Name, Kind = ModelKind.Base, Family = Family, Tier = Tier,
        Tasks = Tasks, VramBytes = VramBytes, InputSize = InputSize, Adapter = Adapter,
        Files = localPaths.ToArray(),
    };
}

/// <summary>
/// The built-in curated "recommended" download list (PHASE8_AI §4). URLs verified against the
/// HuggingFace repos / release manifests (May 2026) — re-check before each release (repos move /
/// relicense). Convenience pointers only; the user confirms the licence per item.
/// </summary>
public static class RecommendedModels
{
    private static ModelFile Hf(string shorthand, string saveAs) => new(shorthand, saveAs);

    public static readonly IReadOnlyList<RecommendedModel> All = new[]
    {
        new RecommendedModel(
            "rmbg-1.4", "RMBG-1.4 (background removal)", "BiRefNet", AiTier.Light,
            new[] { AiTaskKind.Matte },
            new[] { Hf("briaai/RMBG-1.4/onnx/model.onnx", "rmbg-1.4.onnx") },
            SizeBytes: 176_000_000, VramBytes: 1_200_000_000, InputSize: 1024,
            License: "BRIA RMBG-1.4 — non-commercial; commercial use requires a BRIA licence.",
            Adapter: "matte",
            LicenseUrl: "https://huggingface.co/briaai/RMBG-1.4/raw/main/README.md"),

        new RecommendedModel(
            "rmbg-1.4-fp16", "RMBG-1.4 fp16 (background removal, smaller)", "BiRefNet", AiTier.Light,
            new[] { AiTaskKind.Matte },
            new[] { Hf("briaai/RMBG-1.4/onnx/model_fp16.onnx", "rmbg-1.4-fp16.onnx") },
            SizeBytes: 88_200_000, VramBytes: 800_000_000, InputSize: 1024,
            License: "BRIA RMBG-1.4 — non-commercial; commercial use requires a BRIA licence.",
            Adapter: "matte",
            LicenseUrl: "https://huggingface.co/briaai/RMBG-1.4/raw/main/README.md"),

        new RecommendedModel(
            "real-esrgan-x4plus", "Real-ESRGAN x4plus (upscale 4x)", "ESRGAN", AiTier.Light,
            new[] { AiTaskKind.Upscale },
            new[] { new ModelFile(
                "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/real_esrgan_x4plus/releases/v0.54.0/real_esrgan_x4plus-onnx-float.zip",
                "real_esrgan_x4plus-onnx-float.zip") },
            SizeBytes: 64_000_000, VramBytes: 1_500_000_000, InputSize: 128,
            License: "Real-ESRGAN BSD-3-Clause; Qualcomm AI-Hub export under the AI-Hub terms.",
            Adapter: "esrgan",
            LicenseUrl: "https://raw.githubusercontent.com/xinntao/Real-ESRGAN/master/LICENSE"),

        new RecommendedModel(
            "sam2-hiera-large", "SAM 2 Hiera Large (smart selection — best quality)", "SAM2", AiTier.Light,
            new[] { AiTaskKind.Segment },
            // encoder FIRST (Files[0]), decoder SECOND (Files[1]) — the order Sam2Adapter expects
            new[]
            {
                Hf("vietanhdev/segment-anything-2-onnx-models/sam2_hiera_large.encoder.onnx", "sam2_large.encoder.onnx"),
                Hf("vietanhdev/segment-anything-2-onnx-models/sam2_hiera_large.decoder.onnx", "sam2_large.decoder.onnx"),
            },
            SizeBytes: 910_000_000, VramBytes: 5_000_000_000, InputSize: 1024,
            License: "Apache-2.0 (Meta SAM 2).",
            Adapter: "sam2",
            LicenseUrl: "https://huggingface.co/vietanhdev/segment-anything-2-onnx-models/raw/main/README.md"),

        new RecommendedModel(
            "lama-inpaint", "LaMa (object removal / inpaint)", "LaMa", AiTier.Light,
            new[] { AiTaskKind.Inpaint },
            new[] { Hf("Carve/LaMa-ONNX/lama_fp32.onnx", "lama_fp32.onnx") },
            SizeBytes: 207_000_000, VramBytes: 1_500_000_000, InputSize: 512,
            License: "Apache-2.0 (LaMa / big-lama, Carve ONNX export).",
            Adapter: "lama",
            LicenseUrl: "https://huggingface.co/Carve/LaMa-ONNX/raw/main/README.md"),
    };

    public static RecommendedModel? ById(string id)
    {
        foreach (var m in All) if (string.Equals(m.Id, id, System.StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    /// <summary>One model per task (first listed) — the "install the light tier in one go" set.</summary>
    public static IReadOnlyList<RecommendedModel> DefaultSet =>
        All.GroupBy(m => m.Tasks.Count > 0 ? m.Tasks[0] : (AiTaskKind)(-1))
           .Select(g => g.First())
           .ToList();
}
