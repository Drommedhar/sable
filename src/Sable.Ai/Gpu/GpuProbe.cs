using System.Diagnostics;
using System.Globalization;

namespace Sable.Ai.Gpu;

/// <summary>
/// Reports GPU info for the AI pre-flight gate + the model-manager VRAM badges (PHASE8_AI §1.6).
/// On systems with the NVIDIA driver this shells out to <c>nvidia-smi</c> for real free/total VRAM
/// and the compute capability (which selects the right prebuilt CUDA runtime). On macOS (Apple
/// Silicon) it reports the chip name + unified memory via <c>sysctl</c> (there's no discrete VRAM —
/// CoreML draws from the same pool as the app). Values are cached after the first query. The
/// <c>SABLE_AI_FREE_VRAM_MB</c> env var overrides free VRAM (tests / probes).
///
/// <see cref="HasGpu"/> stays false on purpose: whether AI can actually run is decided by a backend
/// reporting <c>IsAvailable</c> (CUDA runtime installed + provider present), not merely by a GPU
/// existing — so readiness can't claim "ready" before the runtime is provisioned.
/// </summary>
public class GpuProbe
{
    private bool _queried, _isNvidia;
    private ulong _free, _total;
    private string? _arch, _name;

    /// <summary>Free VRAM in bytes (best-effort). 0 = unknown.</summary>
    public virtual ulong FreeVramBytes()
    {
        var env = Environment.GetEnvironmentVariable("SABLE_AI_FREE_VRAM_MB");
        if (ulong.TryParse(env, out var mb)) return mb * 1024 * 1024;
        Query();
        return _free;
    }

    /// <summary>Total VRAM in bytes (best-effort). 0 = unknown.</summary>
    public virtual ulong TotalVramBytes() { Query(); return _total; }

    /// <summary>True once a real GPU execution provider + probe is wired (driven by backends, not here).</summary>
    public virtual bool HasGpu => false;

    /// <summary>True if an NVIDIA GPU + driver is present (so the app can offer the CUDA runtime).
    /// False on macOS even though <see cref="AdapterName"/> is set (the Apple GPU isn't NVIDIA/CUDA).</summary>
    public virtual bool IsNvidiaPresent { get { Query(); return _isNvidia; } }

    /// <summary>Adapter name (e.g. "NVIDIA GeForce RTX 5090" or "Apple M4"), or null if none detected.</summary>
    public virtual string? AdapterName { get { Query(); return _name; } }

    /// <summary>
    /// CUDA compute capability with no dot (e.g. "120" for sm_120 / Blackwell), used to pick the
    /// matching prebuilt CUDA ORT. Null if unknown.
    /// </summary>
    public virtual string? ComputeArch { get { Query(); return _arch; } }

    private void Query()
    {
        if (_queried) return;
        _queried = true;
        if (OperatingSystem.IsMacOS()) { QueryMac(); return; }
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.free,memory.total,compute_cap --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            var line = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (line is null) return;
            var cols = line.Split(',', StringSplitOptions.TrimEntries);
            if (cols.Length < 4) return;
            _name = cols[0];
            _isNvidia = true;
            if (ulong.TryParse(cols[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var fmb)) _free = fmb * 1024 * 1024;
            if (ulong.TryParse(cols[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var tmb)) _total = tmb * 1024 * 1024;
            _arch = cols[3].Replace(".", "");   // "12.0" -> "120"
        }
        catch { /* no nvidia-smi / no NVIDIA GPU → leave defaults (unknown) */ }

        // General fallback for total VRAM on non-NVIDIA (AMD/Intel) GPUs: DXGI dedicated memory.
        // Free VRAM stays unknown there (DXGI GetDesc1 gives no live usage); total is what scales work.
        if (_total == 0) { try { _total = DxgiVram.LargestDedicatedBytes(); } catch { } }
    }

    /// <summary>
    /// macOS (Apple Silicon): unified memory, no discrete VRAM. Read the chip name + total unified
    /// memory via <c>sysctl</c>, and budget a conservative fraction as the AI "free VRAM" (CoreML
    /// shares the pool with the OS + the app's own image/GPU buffers) so the VRAM-fit badges and the
    /// pre-flight gate have a real number to compare against. NVIDIA/DXGI paths don't apply here.
    /// </summary>
    private void QueryMac()
    {
        try
        {
            // hw.memsize = total unified memory (bytes); machdep.cpu.brand_string = e.g. "Apple M4".
            var psi = new ProcessStartInfo("sysctl", "-n hw.memsize machdep.cpu.brand_string")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            var lines = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 1 && ulong.TryParse(lines[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var mem))
                _total = mem;
            if (lines.Length >= 2) _name = lines[1].Trim();
        }
        catch { /* sysctl missing → leave defaults (unknown) */ }

        // No separate "free VRAM" on unified memory: budget ~70% of total for AI work, leaving
        // headroom for the OS + the app. The SABLE_AI_FREE_VRAM_MB env override still wins in FreeVramBytes.
        if (_total > 0) _free = (ulong)(_total * 0.7);
    }
}
