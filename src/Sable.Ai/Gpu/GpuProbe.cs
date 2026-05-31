namespace Sable.Ai.Gpu;

/// <summary>
/// Reports free/total VRAM for the AI pre-flight gate (PHASE8_AI §1.6). The real per-OS probe
/// (DXGI QueryVideoMemoryInfo on Windows, NVML, EP query) lands with the gating-polish slice; for
/// now this is a stub overridable via the <c>SABLE_AI_FREE_VRAM_MB</c> env var so the gate logic and
/// its tests have a number to work with. <see cref="HasGpu"/> stays false until a real EP/probe exists.
/// </summary>
public class GpuProbe
{
    /// <summary>Free VRAM in bytes (best-effort). 0 = unknown.</summary>
    public virtual ulong FreeVramBytes()
    {
        var env = Environment.GetEnvironmentVariable("SABLE_AI_FREE_VRAM_MB");
        if (ulong.TryParse(env, out var mb)) return mb * 1024 * 1024;
        return 0;
    }

    /// <summary>Total VRAM in bytes (best-effort). 0 = unknown.</summary>
    public virtual ulong TotalVramBytes() => 0;

    /// <summary>True once a real GPU execution provider + probe is wired (light tier, later slice).</summary>
    public virtual bool HasGpu => false;
}
