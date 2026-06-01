using System.Globalization;

namespace Sable.Core.Ai;

/// <summary>Fit verdict for a model against the current free VRAM (drives the badge colour).</summary>
public enum VramFit { Unknown, Fits, Tight, WontFit }

/// <summary>
/// Pure VRAM-fit badge for the model manager (PHASE8_AI §8.5). The model's stated requirement is
/// always shown; the fit verdict is only assessed when free VRAM is known (<paramref name="freeBytes"/>
/// &gt; 0 — the real per-OS probe lands in the gating-polish slice, §8.9). Reuses <see cref="VramGate"/>
/// so the badge and the pre-flight gate agree. No GPU / no IO — fully unit-testable.
/// </summary>
public readonly record struct VramBadge(VramFit Fit, string Text)
{
    // light models stage one resident part; reserve a modest working-set headroom for activations.
    private const long WorkingSet = 256L * 1024 * 1024;

    public static VramBadge ForModel(long vramBytes, ulong freeBytes)
    {
        string need = Gb(vramBytes) + " GB VRAM";
        if (freeBytes == 0) return new VramBadge(VramFit.Unknown, need);

        var dec = VramGate.Evaluate(new[] { vramBytes }, freeBytes, offload: false, workingSetBytes: WorkingSet);
        if (!dec.Fit)
            return new VramBadge(VramFit.WontFit, $"{need} · won't fit ({Gb((long)freeBytes)} GB free)");

        // "tight" when it leaves under ~15% of free VRAM as headroom
        bool tight = dec.RequiredBytes > (long)(freeBytes * 0.85);
        return new VramBadge(tight ? VramFit.Tight : VramFit.Fits, $"{need} · {(tight ? "tight" : "fits")}");
    }

    private static string Gb(long bytes) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
}
