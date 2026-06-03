using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Sable.Ai.Sidecar.Provisioning;

/// <summary>Accelerated torch wheel family to install (picked by detected GPU vendor).</summary>
public enum TorchVendor { Cuda, Rocm, DirectMl, Mps, Cpu }

/// <summary>
/// Provisions Sable's OWN isolated Python venv with <c>uv</c> when no usable ComfyUI / pinned env exists
/// (PHASE8_AI_SIDECAR §3.3). Vendor-matched torch + diffusers; never touches system Python. This runs
/// multi-GB downloads and is validated by manual integration (not CI); the path/arg helpers below are pure
/// and unit-tested.
/// </summary>
public sealed class UvEnv
{
    private readonly string _venvDir;

    public UvEnv(string venvDir) => _venvDir = venvDir;

    /// <summary>Default sidecar venv location: <c>%AppData%/Sable/sidecar/venv</c> (platform equivalent).</summary>
    public static string DefaultVenvDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sable", "sidecar", "venv");

    /// <summary>The interpreter path inside a venv dir for the host OS.</summary>
    public static string PythonIn(string venvDir) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(venvDir, "Scripts", "python.exe")
            : Path.Combine(venvDir, "bin", "python");

    public string PythonExe => PythonIn(_venvDir);
    public bool IsProvisioned => File.Exists(PythonExe);

    /// <summary>pip args selecting the accelerated torch index for a vendor (PHASE8_AI_SIDECAR §3.3 / Phase 9).</summary>
    public static IReadOnlyList<string> TorchInstallArgs(TorchVendor vendor) => vendor switch
    {
        TorchVendor.Cuda => new[] { "torch", "torchvision", "--index-url", "https://download.pytorch.org/whl/cu124" },
        TorchVendor.Rocm => new[] { "torch", "torchvision", "--index-url", "https://download.pytorch.org/whl/rocm6.1" },
        TorchVendor.DirectMl => new[] { "torch", "torch-directml" },
        TorchVendor.Mps => new[] { "torch", "torchvision" },   // macOS wheels carry MPS
        _ => new[] { "torch", "torchvision", "--index-url", "https://download.pytorch.org/whl/cpu" },
    };

    /// <summary>Locate the <c>uv</c> executable on PATH; null if not found (caller must surface "install uv").</summary>
    public static string? FindUv()
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "uv.exe" : "uv";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>
    /// Create the venv and install vendor torch + diffusers. Returns the venv python path. Streams uv output
    /// to <paramref name="log"/>. Throws if uv is missing or a step fails. (Manual-integration: network + GBs.)
    /// </summary>
    public async Task<string> EnsureAsync(TorchVendor vendor, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (IsProvisioned) return PythonExe;
        var uv = FindUv() ?? throw new InvalidOperationException("uv not found on PATH. Install uv to provision the AI sidecar.");
        Directory.CreateDirectory(Path.GetDirectoryName(_venvDir)!);

        await RunAsync(uv, new[] { "venv", _venvDir }, log, ct).ConfigureAwait(false);

        var torch = new List<string> { "pip", "install", "--python", PythonExe };
        torch.AddRange(TorchInstallArgs(vendor));
        await RunAsync(uv, torch, log, ct).ConfigureAwait(false);

        await RunAsync(uv, new[] { "pip", "install", "--python", PythonExe,
            "diffusers", "transformers", "accelerate", "safetensors", "huggingface-hub" }, log, ct).ConfigureAwait(false);

        return PythonExe;
    }

    private static async Task RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log?.Report(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log?.Report(e.Data); };
        if (!proc.Start()) throw new InvalidOperationException($"failed to start {exe}");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0) throw new InvalidOperationException($"{exe} {string.Join(' ', args)} → exit {proc.ExitCode}");
    }
}
