namespace Sable.Core.Ai;

/// <summary>Outcome of resolving a base model's pipeline components (PHASE8_AI §4).</summary>
public sealed record ComponentResolution(
    bool Ok,
    IReadOnlyList<string> MissingRefs,             // referenced component ids that aren't installed
    IReadOnlyList<string> ResolvedComponentIds,    // installed component models this base pulls in
    IReadOnlyList<string> MissingEncoderFamilies); // AcceptsTextEncoders the resolved set can't satisfy

/// <summary>
/// In-memory model set + the pure rules over it (PHASE8_AI §4): id/task lookup, diffusion component
/// resolution (shared encoder/VAE refs), adapter↔base compatibility, and VRAM-fit. No filesystem,
/// no GPU — <see cref="Sable.Ai"/>'s registry wraps this with JSON load/save. Fully unit-testable.
/// </summary>
public sealed class ModelCatalog
{
    private readonly Dictionary<string, ModelManifest> _byId = new(System.StringComparer.OrdinalIgnoreCase);

    public ModelCatalog() { }
    public ModelCatalog(IEnumerable<ModelManifest> models) { foreach (var m in models) Add(m); }

    public void Add(ModelManifest m) { if (!string.IsNullOrEmpty(m.Id)) _byId[m.Id] = m; }
    public IReadOnlyCollection<ModelManifest> All => _byId.Values;
    public ModelManifest? ById(string id) => id is not null && _byId.TryGetValue(id, out var m) ? m : null;

    /// <summary>Installed models (bases) that can perform a task.</summary>
    public IEnumerable<ModelManifest> ForTask(AiTaskKind task)
        => _byId.Values.Where(m => m.Kind == ModelKind.Base && m.Tasks.Contains(task));

    /// <summary>Installed adapters (LoRA/ControlNet/…) compatible with a given base.</summary>
    public IEnumerable<ModelManifest> AdaptersFor(ModelManifest baseModel)
        => _byId.Values.Where(m => m.Kind == ModelKind.Adapter && IsAdapterCompatible(m, baseModel));

    /// <summary>True if an adapter declares the base's family in <see cref="ModelManifest.AppliesTo"/>.</summary>
    public bool IsAdapterCompatible(ModelManifest adapter, ModelManifest baseModel)
    {
        if (adapter.Kind != ModelKind.Adapter || baseModel.Kind != ModelKind.Base) return false;
        if (adapter.AppliesTo is null || adapter.AppliesTo.Count == 0) return false;
        return adapter.AppliesTo.Any(f => string.Equals(f, baseModel.Family, System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolve a base's components: which referenced component models are missing, which are pulled
    /// in, and whether every <see cref="ModelManifest.AcceptsTextEncoders"/> family is satisfied.
    /// Bundled single-file checkpoints carry their own encoders → trivially Ok.
    /// </summary>
    public ComponentResolution ResolveComponents(ModelManifest baseModel)
    {
        var missingRefs = new List<string>();
        var resolved = new List<string>();
        var satisfiedFamilies = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        var c = baseModel.Components;
        if (c is null || c.IsBundled)
            return new ComponentResolution(true, missingRefs, resolved, System.Array.Empty<string>());

        void Visit(ComponentSource? src)
        {
            if (src is null || !src.IsValid) return;
            if (src.IsPath) return;                       // inline file → trusted, no family to check
            var comp = ById(src.Ref!);
            if (comp is null) { missingRefs.Add(src.Ref!); return; }
            resolved.Add(comp.Id);
            if (!string.IsNullOrEmpty(comp.ComponentFamily)) satisfiedFamilies.Add(comp.ComponentFamily!);
        }

        Visit(c.Denoiser);
        Visit(c.Vae);
        if (c.TextEncoders is not null) foreach (var te in c.TextEncoders) Visit(te);

        // every required encoder family must be present, either via a resolved component or an inline encoder.
        var missingFamilies = new List<string>();
        bool hasInlineEncoder = c.TextEncoders?.Any(te => te is { IsPath: true }) ?? false;
        if (baseModel.AcceptsTextEncoders is { } need)
            foreach (var fam in need)
                if (!satisfiedFamilies.Contains(fam) && !hasInlineEncoder)
                    missingFamilies.Add(fam);

        bool ok = missingRefs.Count == 0 && missingFamilies.Count == 0;
        return new ComponentResolution(ok, missingRefs, resolved, missingFamilies);
    }

    /// <summary>
    /// VRAM parts for a base: its own cost plus every resolved component's cost. Feed to
    /// <see cref="VramGate.Evaluate"/> with the chosen offload mode.
    /// </summary>
    public IReadOnlyList<long> VramParts(ModelManifest baseModel)
    {
        var parts = new List<long> { baseModel.VramBytes };
        foreach (var id in ResolveComponents(baseModel).ResolvedComponentIds)
            if (ById(id) is { } comp) parts.Add(comp.VramBytes);
        return parts;
    }

    /// <summary>Convenience: resolve + VRAM-gate a base in one call.</summary>
    public VramDecision Gate(ModelManifest baseModel, ulong freeBytes, bool offload, long workingSetBytes = 0)
        => VramGate.Evaluate(VramParts(baseModel), freeBytes, offload, workingSetBytes);
}
