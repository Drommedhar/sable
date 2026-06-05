using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sable.Ai.Comfy.Ipc;
using Sable.Ai.Comfy.Workflow;
using Sable.Core.Ai;

namespace Sable.Ai.Comfy;

/// <summary>
/// Generative backend that drives a HEADLESS ComfyUI (PHASE8_AI_COMFY): starts <c>main.py</c> under a
/// resolved ComfyUI install + venv, and turns a <see cref="GenRequest"/> into a workflow graph it runs over
/// the API. Implements the Core <see cref="IGenerativeBackend"/> seam, so <c>AiService</c> drives it exactly
/// like the Diffusers sidecar. The App injects the model/LoRA resolvers + the PNG codec (this project stays
/// codec-free). <see cref="LoadModelAsync"/> is a no-op (models load per-prompt inside the graph).
/// </summary>
public sealed class ComfyBackend : IGenerativeBackend, IDisposable
{
    private Process? _proc;
    private ComfyClient? _client;
    private volatile bool _healthy;

    public string Name => "ComfyUI";
    public AiTier Tier => AiTier.Generative;
    public bool IsAvailable => _healthy;
    public bool RequiresExplicitLoad => false;   // ComfyUI loads the model inside the per-prompt graph

    /// <summary>Progress sink for the next generation (KSampler step progress from the WS). Set per-run by the App.</summary>
    public IProgress<double>? Progress { get; set; }

    /// <summary>Sampler denoise (0..1) for the next run. 1.0 = full regen; lower keeps more of the source.</summary>
    public double Denoise { get; set; } = 1.0;
    /// <summary>The exported API-format ComfyUI workflow (the user's own graph) to run — Sable injects the
    /// image/prompt/seed/steps/cfg/denoise + model/LoRA names. Required (every preset carries one).</summary>
    public string? WorkflowJsonPath { get; set; }

    /// <summary>The running ComfyUI base URL (for "Open ComfyUI"), or null if not started.</summary>
    public Uri? BaseUri => _client?.BaseUri;

    /// <summary>GB of VRAM ComfyUI reserves (leaves free) for Sable's GPU canvas, so the editor doesn't OOM
    /// during a generation. Passed as <c>--reserve-vram</c>. 0 = don't reserve.</summary>
    public double ReserveVramGb { get; set; } = 2.0;

    // --- App-injected glue (keeps this project free of registry + image-codec deps) ---
    /// <summary>baseModelId → how ComfyUI loads it (ckpt/unet name + clip/vae for assembled).</summary>
    public Func<string, ComfyModelRef?>? ResolveModel { get; set; }
    /// <summary>LoRA model id → its <c>loras/</c> file name.</summary>
    public Func<string, string>? LoraName { get; set; }
    /// <summary>PNG bytes → (RGBA, w, h).</summary>
    public Func<byte[], (byte[] Rgba, int W, int H)>? DecodePng { get; set; }
    /// <summary>(RGBA, w, h) → PNG bytes.</summary>
    public Func<byte[], int, int, byte[]>? EncodePng { get; set; }

    /// <summary>Tail of ComfyUI's stdout/stderr from the last start (for surfacing a boot failure).</summary>
    public string LastOutputTail { get; private set; } = "";
    /// <summary>Where the full ComfyUI log is written.</summary>
    public static string LogPath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Sable", "comfy.log");

    /// <summary>Launch headless ComfyUI and wait until it answers. Captures ComfyUI's own output to
    /// <see cref="LogPath"/> and fails FAST (no 120s wait) if the process exits during boot — so a crash /
    /// missing-dep / custom-node import error surfaces instead of a silent hang.</summary>
    public async Task<bool> StartAsync(string pythonExe, string comfyDir, TimeSpan? startupTimeout = null, CancellationToken ct = default)
    {
        Stop();
        var main = Path.Combine(comfyDir, "main.py");
        if (!File.Exists(main)) throw new FileNotFoundException("ComfyUI main.py not found", main);

        int port = FreeTcpPort();
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe, WorkingDirectory = comfyDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-u");                 // unbuffered, so we see output live
        psi.ArgumentList.Add(main);
        psi.ArgumentList.Add("--listen"); psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
        // leave VRAM headroom for Sable's own GPU canvas so the editor doesn't OOM while a generation runs
        if (ReserveVramGb > 0)
        {
            psi.ArgumentList.Add("--reserve-vram");
            psi.ArgumentList.Add(ReserveVramGb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }

        var tail = new System.Collections.Generic.Queue<string>();
        System.IO.StreamWriter? logw = null;
        try { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); logw = new System.IO.StreamWriter(LogPath, append: false); } catch { }
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (tail) { tail.Enqueue(line); while (tail.Count > 40) tail.Dequeue(); LastOutputTail = string.Join("\n", tail); }
            try { lock (tail) { logw?.WriteLine(line); logw?.Flush(); } } catch { }
        }

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        _proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        if (!_proc.Start()) throw new InvalidOperationException("failed to start ComfyUI");
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        _client = new ComfyClient(new Uri($"http://127.0.0.1:{port}/"));
        var deadline = DateTime.UtcNow + (startupTimeout ?? TimeSpan.FromSeconds(180));   // first boot loads nodes
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_proc.HasExited)   // crashed during boot → don't wait the full timeout
            {
                try { logw?.Dispose(); } catch { }
                return false;
            }
            if ((await _client.SystemStatsAsync(ct).ConfigureAwait(false)).Ok) { _healthy = true; return true; }
            try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return false; }
        }
        return false;
    }

    public async Task<ulong> ProbeFreeVramAsync(CancellationToken ct = default)
    {
        if (_client is null || !_healthy) return 0;
        var (ok, free) = await _client.SystemStatsAsync(ct).ConfigureAwait(false);
        return ok && free > 0 ? (ulong)free : 0;
    }

    /// <summary>No-op: ComfyUI loads models inside the per-prompt graph, not via a separate load step.</summary>
    public Task<LoadModelResult> LoadModelAsync(LoadModelRequest req, CancellationToken ct = default)
        => Task.FromResult(new LoadModelResult(true));

    /// <summary>Unload models + free VRAM (call when the user closes the gen dialog — ComfyUI keeps weights
    /// resident across runs otherwise).</summary>
    public Task FreeMemoryAsync(CancellationToken ct = default)
        => _client?.FreeAsync(true, ct) ?? Task.CompletedTask;

    public async Task<AiImage> GenerateAsync(GenRequest req, CancellationToken ct = default)
    {
        if (_client is null || !_healthy) throw new InvalidOperationException("ComfyUI is not running.");
        if (DecodePng is null || EncodePng is null) throw new InvalidOperationException("ComfyBackend not wired (App must set the codec).");
        if (string.IsNullOrWhiteSpace(WorkflowJsonPath) || !System.IO.File.Exists(WorkflowJsonPath))
            throw new InvalidOperationException("This preset has no workflow file. Configure one in AI ▸ Models ▸ Generative.");

        // run the user's exported workflow (the user's own graph): inject our image/prompt/seed/params + the
        // preset's model + LoRA names (overriding the workflow's baked, possibly wrong-OS, loader names).
        // txt2img presets send no image (no LoadImage to fill).
        var iname = req.Image is { } img
            ? await _client.UploadImageAsync(EncodePng(img.Rgba, img.Width, img.Height), "sable_in.png", ct).ConfigureAwait(false)
            : "";
        var apiJson = await System.IO.File.ReadAllTextAsync(WorkflowJsonPath, ct).ConfigureAwait(false);
        var tLoras = (req.Loras ?? (IReadOnlyList<AdapterRef>)System.Array.Empty<AdapterRef>())
            .Select(l => (Name: LoraName?.Invoke(l.ModelId) ?? "", l.Weight))
            .Where(l => !string.IsNullOrEmpty(l.Name)).ToList();
        var mref = string.IsNullOrEmpty(req.BaseModelId) ? null : ResolveModel?.Invoke(req.BaseModelId);
        var graph = WorkflowTemplate.Build(apiJson, new WorkflowTemplate.Inject(
            req.Prompt ?? "", req.Negative ?? "", iname, req.Seed, req.Steps, req.Cfg, Denoise, tLoras,
            UnetName: mref?.Kind == ComfyModelKind.Unet ? mref.Name : null,
            CheckpointName: mref?.Kind == ComfyModelKind.Checkpoint ? mref.Name : null,
            ClipNames: mref?.ClipNames, VaeName: mref?.VaeName));
        var png = await _client.RunPromptAsync(graph, Progress, ct).ConfigureAwait(false);
        var (outRgba, ow, oh) = DecodePng(png);
        return new AiImage(outRgba, ow, oh);
    }

    public void Stop()
    {
        _healthy = false;
        try { _client?.Dispose(); } catch { }
        _client = null;
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    public void Dispose() => Stop();

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
