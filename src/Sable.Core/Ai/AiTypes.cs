using System.Threading;
using System.Threading.Tasks;

namespace Sable.Core.Ai;

/// <summary>Which execution tier a model runs in (PLAN §6.1 / PHASE8_AI §0).</summary>
public enum AiTier { Light, Generative }

/// <summary>What an AI model can do. A model declares one or more.</summary>
public enum AiTaskKind { Segment, Matte, Upscale, Denoise, Inpaint, Txt2Img, Outpaint }

/// <summary>SAM2-style prompt geometry (doc-normalised 0..1 coords).</summary>
public enum AiPromptKind { Point, Box, Scribble }

/// <summary>A model entry's role. Adapters/components attach to a base, not run standalone.</summary>
public enum ModelKind { Base, Adapter, Component }

/// <summary>Adapter family that rides on top of a diffusion base (PHASE8_AI §4).</summary>
public enum AdapterType { None, Lora, ControlNet, IpAdapter }

/// <summary>Universal pixel payload between the editor and a model: straight-alpha RGBA8.</summary>
public sealed record AiImage(byte[] Rgba, int Width, int Height);

/// <summary>Single-channel coverage 0..255 (selection / matte / inpaint mask).</summary>
public sealed record AiMask(byte[] Coverage, int Width, int Height);

/// <summary>A SAM2 prompt: a point/box/scribble, positive (include) or negative (exclude).</summary>
public sealed record AiPrompt(AiPromptKind Kind, float X0, float Y0, float X1, float Y1, bool Positive);

/// <summary>Knobs for a raster model (upscale factor, effect strength, plus free-form extras).</summary>
public sealed record AiParams(int Factor = 1, double Strength = 1.0, IReadOnlyDictionary<string, double>? Extra = null);

/// <summary>A LoRA (or other adapter) reference + its blend weight, applied on top of a base.</summary>
public sealed record AdapterRef(string ModelId, double Weight);

/// <summary>A generative (diffusion) request: base + prompt + optional image/mask + a LoRA stack.</summary>
public sealed record GenRequest(
    string BaseModelId,
    AiTaskKind Task,
    string Prompt,
    string Negative = "",
    int Steps = 25,
    double Cfg = 7.0,
    long Seed = -1,
    AiImage? Image = null,
    AiMask? Mask = null,
    IReadOnlyList<AdapterRef>? Loras = null,
    bool Offload = false);

// --- the three model shapes every feature reduces to (PHASE8_AI §1.1) ---

/// <summary>Segmentation / matting: image + prompts → coverage mask. (SAM2, BiRefNet, RMBG.)</summary>
public interface IMaskModel
{
    Task<AiMask> SegmentAsync(AiImage img, IReadOnlyList<AiPrompt> prompts, CancellationToken ct = default);
}

/// <summary>Image→image: upscale / denoise / non-generative inpaint. (ESRGAN, LaMa.)</summary>
public interface IRasterModel
{
    Task<AiImage> ApplyAsync(AiImage img, AiMask? mask, AiParams p, CancellationToken ct = default);
}

/// <summary>Diffusion: prompt (+ optional image/mask + LoRA stack) → image. (Sidecar.)</summary>
public interface IGenerativeModel
{
    Task<AiImage> GenerateAsync(GenRequest req, CancellationToken ct = default);
}

/// <summary>A runtime that hosts models on a tier (in-proc ONNX, or the opt-in sidecar).</summary>
public interface IAiBackend
{
    string Name { get; }
    AiTier Tier { get; }
    bool IsAvailable { get; }
    Task<ulong> ProbeFreeVramAsync(CancellationToken ct = default);
}
