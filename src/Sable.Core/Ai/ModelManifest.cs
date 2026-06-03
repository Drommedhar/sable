namespace Sable.Core.Ai;

/// <summary>
/// One component slot of a diffusion pipeline: either a local file <see cref="Path"/> or a
/// <see cref="Ref"/> to a separately-installed <c>component</c> model id (shared encoders/VAE).
/// Exactly one of the two is set (PHASE8_AI §4).
/// </summary>
public sealed class ComponentSource
{
    public string? Path { get; init; }
    public string? Ref { get; init; }

    public bool IsRef => !string.IsNullOrEmpty(Ref);
    public bool IsPath => !string.IsNullOrEmpty(Path);
    public bool IsValid => IsRef ^ IsPath;   // exactly one
}

/// <summary>
/// A diffusion base's components. A single-file checkpoint bundles denoiser+encoders+VAE
/// (<see cref="Checkpoint"/> set, the rest implied); otherwise the pieces are explicit and may
/// reference shared component models (e.g. a standalone T5-XXL text encoder).
/// </summary>
public sealed class ModelComponents
{
    public ComponentSource? Checkpoint { get; init; }     // single-file bundle (SD1.5/SDXL)
    public ComponentSource? Denoiser { get; init; }       // UNet / DiT transformer
    public IReadOnlyList<ComponentSource>? TextEncoders { get; init; }
    public ComponentSource? Vae { get; init; }
    public string? Scheduler { get; init; }

    /// <summary>True when a single-file checkpoint carries every component itself.</summary>
    public bool IsBundled => Checkpoint is { IsValid: true };
}

/// <summary>
/// One model/adapter/component entry (PHASE8_AI §4). Drives the registry: compatibility, component
/// resolution, VRAM gating. A pure POCO — no runtime/ORT/torch deps — so it lives in Core and is
/// fully unit-testable. Serialized as <c>model.json</c>.
/// </summary>
public sealed class ModelManifest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ModelKind Kind { get; init; } = ModelKind.Base;
    public string Family { get; init; } = "";            // SAM2 / BiRefNet / ESRGAN / LaMa / SD1.5 / SDXL / SD3 / Flux / ...
    public AiTier Tier { get; init; } = AiTier.Light;
    public IReadOnlyList<AiTaskKind> Tasks { get; init; } = System.Array.Empty<AiTaskKind>();
    public long VramBytes { get; init; }                 // required VRAM for this piece
    public int InputSize { get; init; }                  // model's native input resolution (0 = n/a)
    public string? Adapter { get; init; }                // which adapter code runs it
    public string? SourceId { get; init; }               // which ModelSource this came from (null = native/legacy)

    // --- base (diffusion / ONNX) ---
    public ModelComponents? Components { get; init; }
    public IReadOnlyList<string>? AcceptsTextEncoders { get; init; }   // encoder families this base needs (e.g. CLIP-L, T5-XXL)
    public string? AcceptsVae { get; init; }

    // --- adapter (LoRA / ControlNet / IP-Adapter) ---
    public AdapterType AdapterType { get; init; } = AdapterType.None;
    public IReadOnlyList<string>? AppliesTo { get; init; }             // base families this adapter is valid on
    public double DefaultWeight { get; init; } = 1.0;
    public IReadOnlyList<string>? TriggerWords { get; init; }

    // --- component (shared text encoder / VAE installed once) ---
    public string? ComponentFamily { get; init; }                     // CLIP-L / CLIP-bigG / T5-XXL / VAE-SDXL / ...
    public IReadOnlyList<string>? Files { get; init; }
}
