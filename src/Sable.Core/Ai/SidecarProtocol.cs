using System.Threading.Tasks;

namespace Sable.Core.Ai;

/// <summary>
/// DTOs for the generative-sidecar IPC (PHASE8_AI_SIDECAR §3.4). Pure POCOs in Core so both the client
/// (<c>Sable.Ai.Sidecar</c>) and unit tests can serialize/deserialize them without any Python. Slice S2 only
/// needs <see cref="SidecarHealth"/> + <see cref="VramReport"/>; load/generate DTOs arrive with S3/S4.
/// </summary>
public sealed record SidecarHealth(bool Ok, string Version = "", string Device = "");

/// <summary>GPU memory as the sidecar sees it (bytes). Free drives the pre-flight VRAM gate.</summary>
public sealed record VramReport(long TotalBytes, long FreeBytes, string Device = "");

/// <summary>How the server should construct a diffusion pipeline (PHASE8_AI_SIDECAR §3.5).</summary>
public enum PipelineKind { SingleFile, Pretrained, Assembled }

/// <summary>Resolved component file paths for one pipeline. Exactly the set the chosen <see cref="PipelineKind"/>
/// needs is populated (single-file → <see cref="Checkpoint"/>; folder → <see cref="PretrainedDir"/>;
/// assembled → <see cref="Denoiser"/> + <see cref="TextEncoders"/> + <see cref="Vae"/>).</summary>
public sealed record ComponentPaths(
    string? Checkpoint = null,
    string? PretrainedDir = null,
    string? Denoiser = null,
    IReadOnlyList<string>? TextEncoders = null,
    string? Vae = null,
    string? Scheduler = null);

/// <summary>A LoRA resolved to its on-disk path + blend weight (the server can't resolve catalog ids).</summary>
public sealed record LoraSpec(string Path, double Weight, string Name = "");

/// <summary>Request to load a base (+ optional LoRA stack) into the sidecar (§3.5). All paths are already
/// resolved by <see cref="LoadPlan"/> from the registry, so the server just constructs from explicit paths.</summary>
public sealed record LoadModelRequest(
    string ModelId,
    string Family,
    PipelineKind Kind,
    ComponentPaths Paths,
    bool Offload = false,
    IReadOnlyList<LoraSpec>? Loras = null);

/// <summary>Sidecar's reply to <c>load_model</c>: success + the ACTUAL peak VRAM so the gate self-corrects,
/// or a structured error (e.g. a missing component) the app surfaces with an import prompt.</summary>
public sealed record LoadModelResult(bool Ok, long PeakVramBytes = 0, string Device = "", string Error = "");

/// <summary>Sidecar's reply to a generate (<c>inpaint</c>/<c>outpaint</c>/<c>txt2img</c>): the result image as
/// straight-alpha RGBA8 (base64 over JSON), the seed actually used, or a structured error.</summary>
public sealed record GenResult(byte[] Rgba, int Width, int Height, long Seed = -1, string Error = "")
{
    public bool Ok => string.IsNullOrEmpty(Error) && Rgba.Length > 0;
}

/// <summary>
/// A generative backend the editor can drive (PHASE8_AI_SIDECAR §3): the diffusion <see cref="IGenerativeModel"/>
/// plus the lifecycle the orchestrator needs (availability + loading a resolved pipeline). The sidecar
/// implements this; <c>Sable.Ai</c>'s <c>AiService</c> depends only on this Core seam, never on the sidecar
/// project — so the App injects the concrete backend.
/// </summary>
public interface IGenerativeBackend : IGenerativeModel
{
    bool IsAvailable { get; }
    Task<LoadModelResult> LoadModelAsync(LoadModelRequest req, System.Threading.CancellationToken ct = default);
}
