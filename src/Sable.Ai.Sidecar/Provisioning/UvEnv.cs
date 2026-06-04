using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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

    /// <summary>Pick the accelerated torch wheel family for this host (NVIDIA→CUDA, mac→MPS, Win→DirectML,
    /// else ROCm). Pure given the NVIDIA flag → unit-tested.</summary>
    public static TorchVendor DefaultVendor(bool nvidiaPresent)
    {
        if (nvidiaPresent) return TorchVendor.Cuda;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return TorchVendor.Mps;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return TorchVendor.DirectMl;
        return TorchVendor.Rocm;
    }

    /// <summary>pip args selecting the accelerated torch index for a vendor (PHASE8_AI_SIDECAR §3.3 / Phase 9).</summary>
    /// <summary>pip args for the torch trio. <paramref name="cudaTag"/> = the detected CUDA wheel
    /// (<see cref="CudaWheel.Detect"/>); torch+torchvision+torchaudio MUST come from the SAME index (ComfyUI
    /// imports torchaudio; a mismatched build fails its native ext load — "WinError 127").</summary>
    public static IReadOnlyList<string> TorchInstallArgs(TorchVendor vendor, string cudaTag = "cu128") => vendor switch
    {
        TorchVendor.Cuda => new[] { "torch", "torchvision", "torchaudio", "--index-url", $"https://download.pytorch.org/whl/{cudaTag}" },
        TorchVendor.Rocm => new[] { "torch", "torchvision", "torchaudio", "--index-url", "https://download.pytorch.org/whl/rocm6.1" },
        TorchVendor.DirectMl => new[] { "torch", "torch-directml", "torchaudio" },
        TorchVendor.Mps => new[] { "torch", "torchvision", "torchaudio" },   // macOS wheels carry MPS
        _ => new[] { "torch", "torchvision", "torchaudio", "--index-url", "https://download.pytorch.org/whl/cpu" },
    };

    /// <summary>Locate the <c>uv</c> executable on PATH; null if not found.</summary>
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

    /// <summary>Where Sable downloads its own copy of <c>uv</c> when it isn't on PATH.</summary>
    public static string UvToolDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sable", "sidecar", "uv");

    public static string UvExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "uv.exe" : "uv";

    /// <summary>The Astral release asset for this OS+arch (PHASE8_AI_SIDECAR §3.3). Pure → unit-tested.</summary>
    public static string UvAssetName(OSPlatform os, Architecture arch)
    {
        string a = arch == Architecture.Arm64 ? "aarch64" : "x86_64";
        if (os == OSPlatform.Windows) return $"uv-{a}-pc-windows-msvc.zip";
        if (os == OSPlatform.OSX) return $"uv-{a}-apple-darwin.tar.gz";
        return $"uv-{a}-unknown-linux-gnu.tar.gz";
    }

    private static OSPlatform HostOsPlatform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX : OSPlatform.Linux;

    /// <summary>
    /// Ensure a usable <c>uv</c>: PATH first, then Sable's own downloaded copy, else fetch the Astral
    /// standalone binary into <see cref="UvToolDir"/> and extract it. Returns the uv path. Network (manual-
    /// integration); the asset-name logic is pure-tested.
    /// </summary>
    public static async Task<string> EnsureUvAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        var onPath = FindUv();
        if (onPath is not null) return onPath;

        var local = Path.Combine(UvToolDir, UvExeName);
        if (File.Exists(local)) return local;

        Directory.CreateDirectory(UvToolDir);
        var asset = UvAssetName(HostOsPlatform, RuntimeInformation.OSArchitecture);
        var url = $"https://github.com/astral-sh/uv/releases/latest/download/{asset}";
        log?.Report($"Downloading uv ({asset})…");

        var archive = Path.Combine(UvToolDir, asset);
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        await using (var s = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
        await using (var f = File.Create(archive))
            await s.CopyToAsync(f, ct).ConfigureAwait(false);

        log?.Report("Extracting uv…");
        if (asset.EndsWith(".zip"))
            ZipFile.ExtractToDirectory(archive, UvToolDir, overwriteFiles: true);
        else
            await ExtractTarGzAsync(archive, UvToolDir, ct).ConfigureAwait(false);
        try { File.Delete(archive); } catch { }

        // the tar.gz nests the binary in a folder; find it
        var uv = Directory.EnumerateFiles(UvToolDir, UvExeName, SearchOption.AllDirectories).FirstOrDefault()
                 ?? throw new InvalidOperationException("uv binary not found after extraction");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            try { var psi = new ProcessStartInfo("chmod", $"+x \"{uv}\"") { UseShellExecute = false }; Process.Start(psi)?.WaitForExit(); } catch { }
        return uv;
    }

    private static async Task ExtractTarGzAsync(string archive, string dest, CancellationToken ct)
    {
        await using var fs = File.OpenRead(archive);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, dest, overwriteFiles: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Create the venv and install vendor torch + diffusers. Returns the venv python path. Streams uv output
    /// to <paramref name="log"/>. Throws if uv is missing or a step fails. (Manual-integration: network + GBs.)
    /// </summary>
    public async Task<string> EnsureAsync(TorchVendor vendor, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (IsProvisioned) return PythonExe;
        var uv = await EnsureUvAsync(log, ct).ConfigureAwait(false);   // bootstrap uv if it isn't installed
        Directory.CreateDirectory(Path.GetDirectoryName(_venvDir)!);

        log?.Report("Creating Python environment…");
        await RunAsync(uv, new[] { "venv", "--python", "3.11", _venvDir }, log, ct).ConfigureAwait(false);

        var cudaTag = vendor == TorchVendor.Cuda ? CudaWheel.Detect() : "cu128";
        log?.Report($"Installing PyTorch ({(vendor == TorchVendor.Cuda ? cudaTag : vendor.ToString())})…");
        var torch = new List<string> { "pip", "install", "--python", PythonExe };
        torch.AddRange(TorchInstallArgs(vendor, cudaTag));
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
