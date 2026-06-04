using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sable.Ai.Sidecar.Provisioning;

namespace Sable.Ai.Comfy.Provisioning;

/// <summary>
/// Provisions a local ComfyUI Sable can run (PHASE8_AI_COMFY §2.4): git-clone ComfyUI, make a uv venv,
/// install vendor torch + ComfyUI requirements, write an <c>extra_model_paths.yaml</c> pointing at the user's
/// models, and link the user's <c>custom_nodes</c> (+ best-effort install their requirements). Used when no
/// same-OS ComfyUI is found (e.g. the user's Linux install on Windows). Network + multi-GB → manual
/// integration; the pure parts live in <see cref="ComfyReuse"/>.
/// </summary>
public sealed class ComfyProvisioner
{
    private const string ComfyGit = "https://github.com/comfyanonymous/ComfyUI.git";
    private const string ManagerGit = "https://github.com/ltdrdata/ComfyUI-Manager.git";

    public string ComfyDir { get; }
    public string VenvDir => Path.Combine(ComfyDir, "venv");
    public string PythonExe => UvEnv.PythonIn(VenvDir);
    public bool IsProvisioned => File.Exists(Path.Combine(ComfyDir, "main.py")) && File.Exists(PythonExe);

    public ComfyProvisioner(string? comfyDir = null) => ComfyDir = comfyDir ?? ComfyLocator.OwnComfyDir;

    /// <summary>Clone + venv + torch + requirements + reuse the user's models/custom_nodes. Returns the install.</summary>
    public async Task<ComfyInstall> EnsureAsync(string userModelsDir, TorchVendor vendor, IProgress<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ComfyDir)!);

        if (!File.Exists(Path.Combine(ComfyDir, "main.py")))
        {
            log?.Report("Cloning ComfyUI…");
            await RunAsync("git", new[] { "clone", "--depth", "1", ComfyGit, ComfyDir }, ComfyDir: null, log, ct).ConfigureAwait(false);
        }

        var uv = await UvEnv.EnsureUvAsync(log, ct).ConfigureAwait(false);
        if (!File.Exists(PythonExe))
        {
            log?.Report("Creating Python environment…");
            await RunAsync(uv, new[] { "venv", "--python", "3.12", VenvDir }, null, log, ct).ConfigureAwait(false);
        }

        var cudaTag = vendor == TorchVendor.Cuda ? CudaWheel.Detect() : "cu128";
        log?.Report($"Installing PyTorch ({(vendor == TorchVendor.Cuda ? cudaTag : vendor.ToString())})…");
        var torch = new List<string> { "pip", "install", "--python", PythonExe };
        torch.AddRange(UvEnv.TorchInstallArgs(vendor, cudaTag));
        await RunAsync(uv, torch, null, log, ct).ConfigureAwait(false);

        log?.Report("Installing ComfyUI requirements…");
        await RunAsync(uv, new[] { "pip", "install", "--python", PythonExe, "-r", Path.Combine(ComfyDir, "requirements.txt") }, null, log, ct).ConfigureAwait(false);

        // ship ComfyUI-Manager so the user can install any custom nodes their workflows need via the web UI
        await EnsureComfyManagerAsync(uv, log, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(userModelsDir) && Directory.Exists(userModelsDir))
        {
            WriteExtraModelPaths(userModelsDir);
            await LinkCustomNodesAsync(userModelsDir, uv, log, ct).ConfigureAwait(false);
        }

        return new ComfyInstall(ComfyDir, PythonExe);
    }

    /// <summary>Point the provisioned ComfyUI at the user's models folder (no copy).</summary>
    public void WriteExtraModelPaths(string userModelsDir)
    {
        try { File.WriteAllText(Path.Combine(ComfyDir, "extra_model_paths.yaml"), ComfyReuse.BuildExtraModelPaths(userModelsDir)); }
        catch { }
    }

    /// <summary>Clone ComfyUI-Manager into the provisioned ComfyUI's custom_nodes (so the user can install
    /// custom nodes their workflows need from the web UI). Best-effort.</summary>
    private async Task EnsureComfyManagerAsync(string uv, IProgress<string>? log, CancellationToken ct)
    {
        var dir = Path.Combine(ComfyDir, "custom_nodes", "ComfyUI-Manager");
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(Path.Combine(ComfyDir, "custom_nodes"));
        log?.Report("Installing ComfyUI-Manager…");
        try
        {
            await RunAsync("git", new[] { "clone", "--depth", "1", ManagerGit, dir }, null, log, ct).ConfigureAwait(false);
            var reqs = Path.Combine(dir, "requirements.txt");
            if (File.Exists(reqs))
                await RunAsync(uv, new[] { "pip", "install", "--python", PythonExe, "-r", reqs }, null, log, ct).ConfigureAwait(false);
        }
        catch (System.Exception ex) { log?.Report($"ComfyUI-Manager install failed: {ex.Message}"); }
    }

    private async Task LinkCustomNodesAsync(string userModelsDir, string uv, IProgress<string>? log, CancellationToken ct)
    {
        var userRoot = ComfyLocator.RootFrom(userModelsDir);
        var userCnd = Path.Combine(userRoot, "custom_nodes");
        if (!Directory.Exists(userCnd)) return;
        var ownCnd = Path.Combine(ComfyDir, "custom_nodes");
        Directory.CreateDirectory(ownCnd);

        IEnumerable<string> names;
        try { names = Directory.EnumerateDirectories(userCnd).Select(Path.GetFileName).Where(n => n is not null)!; }
        catch { return; }

        foreach (var (src, dst) in ComfyReuse.PlanCustomNodeLinks(userCnd, names!, ownCnd))
        {
            if (Directory.Exists(dst) || File.Exists(dst)) continue;
            try { Directory.CreateSymbolicLink(dst, src); }
            catch (System.Exception ex) { log?.Report($"link {Path.GetFileName(src)} failed: {ex.Message}"); continue; }

            var reqs = Path.Combine(src, "requirements.txt");
            if (File.Exists(reqs))
            {
                log?.Report($"Installing deps for {Path.GetFileName(src)}…");
                try { await RunAsync(uv, new[] { "pip", "install", "--python", PythonExe, "-r", reqs }, null, log, ct).ConfigureAwait(false); }
                catch (System.Exception ex) { log?.Report($"deps for {Path.GetFileName(src)} failed: {ex.Message}"); }
            }
        }
    }

    private static async Task RunAsync(string exe, IReadOnlyList<string> args, string? ComfyDir, IProgress<string>? log, CancellationToken ct)
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
