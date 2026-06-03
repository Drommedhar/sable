using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Sable.Ai.Sidecar.Provisioning;

/// <summary>Which OS family a Python interpreter / venv is built for. A venv is NOT portable across this
/// boundary (PHASE8_AI_SIDECAR §0.4): a Posix <c>bin/python</c> + ELF torch can't run on Windows.</summary>
public enum HostOs { Windows, Linux, MacOS }

/// <summary>What a discovered Python environment can do, from the probe script (§3.1).</summary>
public sealed record EnvCaps(
    string TorchVersion,
    string DiffusersVersion,
    string? CudaVersion,
    bool Cuda,
    bool Mps,
    bool Rocm,
    bool DirectMl)
{
    /// <summary>Minimum diffusers we accept when reusing someone else's env.</summary>
    public const string MinDiffusers = "0.27.0";

    public bool HasAccelerator => Cuda || Mps || Rocm || DirectMl;

    /// <summary>Usable = diffusers present + new enough + a GPU accelerator (no CPU-only inference, §0.5).</summary>
    public bool IsUsable(out string reason)
    {
        if (string.IsNullOrWhiteSpace(TorchVersion)) { reason = "torch not installed"; return false; }
        if (string.IsNullOrWhiteSpace(DiffusersVersion)) { reason = "diffusers not installed"; return false; }
        if (CompareVersions(DiffusersVersion, MinDiffusers) < 0) { reason = $"diffusers {DiffusersVersion} < {MinDiffusers}"; return false; }
        if (!HasAccelerator) { reason = "no GPU accelerator (CPU-only torch)"; return false; }
        reason = "";
        return true;
    }

    /// <summary>Compare leading dotted-numeric versions; non-numeric tails ignored. -1/0/1.</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] Parse(string s)
        {
            var head = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            var parts = head.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var nums = new int[3];
            for (int i = 0; i < 3 && i < parts.Length; i++) int.TryParse(parts[i], out nums[i]);
            return nums;
        }
        var (x, y) = (Parse(a), Parse(b));
        for (int i = 0; i < 3; i++) { int c = x[i].CompareTo(y[i]); if (c != 0) return c; }
        return 0;
    }
}

/// <summary>A resolved interpreter the sidecar server will run under (§3.1).</summary>
public sealed record PythonEnv(string PythonExe, string Origin /* pinned | comfyui | sable */, EnvCaps Caps);

/// <summary>Runs the probe script against a python and returns its capabilities (null = couldn't probe).</summary>
public interface IEnvProbe
{
    Task<EnvCaps?> ProbeAsync(string pythonExe, CancellationToken ct = default);
}
