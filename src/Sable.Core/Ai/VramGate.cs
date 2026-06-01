namespace Sable.Core.Ai;

/// <summary>Result of a VRAM pre-flight check (PHASE8_AI §1.6).</summary>
public readonly record struct VramDecision(bool Fit, long RequiredBytes, ulong FreeBytes, string Message);

/// <summary>
/// Pure VRAM gating (PHASE8_AI §1.6). "GPU-only, no CPU fallback" gates compute, not weight staging:
/// with <c>offload</c>, idle pipeline components live in RAM, so peak VRAM is the LARGEST single part
/// plus the working set — not the naive sum. Without offload, every part is resident at once.
/// No GPU calls here — feed it part sizes + free bytes; <see cref="GpuProbe"/> supplies the latter.
/// </summary>
public static class VramGate
{
    /// <param name="partBytes">Per-component weight costs (denoiser/base, text encoders, VAE, …).</param>
    /// <param name="freeBytes">Currently-free VRAM on the chosen device.</param>
    /// <param name="offload">Sequential component offload (idle weights → RAM).</param>
    /// <param name="workingSetBytes">Latents / activations headroom estimate.</param>
    public static VramDecision Evaluate(
        IReadOnlyList<long> partBytes, ulong freeBytes, bool offload, long workingSetBytes = 0)
    {
        long resident;
        if (offload)
        {
            long maxPart = 0;
            foreach (var p in partBytes) if (p > maxPart) maxPart = p;
            resident = maxPart;
        }
        else
        {
            resident = 0;
            foreach (var p in partBytes) resident += p;
        }
        long required = resident + workingSetBytes;
        bool fit = required <= (long)freeBytes && required >= 0;
        string msg = fit
            ? $"Fits: needs ~{Mb(required)} MB, {Mb((long)freeBytes)} MB free" + (offload ? " (offload)" : "")
            : $"Won't fit: needs ~{Mb(required)} MB but only {Mb((long)freeBytes)} MB free." +
              (offload ? " Try a smaller model/encoder." : " Try enabling offload or a smaller model.");
        return new VramDecision(fit, required, freeBytes, msg);
    }

    private static long Mb(long b) => b / (1024 * 1024);
}
