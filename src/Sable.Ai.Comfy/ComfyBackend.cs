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

    public async Task<AiImage> GenerateAsync(GenRequest req, CancellationToken ct = default)
    {
        if (_client is null || !_healthy) throw new InvalidOperationException("ComfyUI is not running.");
        if (ResolveModel is null || DecodePng is null) throw new InvalidOperationException("ComfyBackend not wired (App must set resolvers).");

        var model = ResolveModel(req.BaseModelId) ?? throw new InvalidOperationException($"Unknown model '{req.BaseModelId}'.");
        if (!ArchTemplates.IsImageArch(model.Family))
            throw new InvalidOperationException($"'{model.Family}' is a video/unsupported architecture — no image graph yet.");
        if (model.Kind == ComfyModelKind.Unet && (model.ClipNames is null || model.ClipNames.Count == 0))
            throw new InvalidOperationException(
                $"'{model.Name}' is a standalone transformer (diffusion_models) — it needs a text encoder + VAE chosen. " +
                "That picker isn't built yet; pick a full checkpoint (checkpoints/ folder) for now.");

        Dictionary<string, object> graph;
        if (req.Task == AiTaskKind.Inpaint && req.Image is { } img && req.Mask is { } mask)
        {
            if (EncodePng is null) throw new InvalidOperationException("ComfyBackend not wired (no PNG encoder).");
            var rgba = CombineAlpha(img.Rgba, mask.Coverage, img.Width, img.Height);
            var name = await _client.UploadImageAsync(EncodePng(rgba, img.Width, img.Height), "sable_inpaint.png", ct).ConfigureAwait(false);
            graph = WorkflowBuilder.Inpaint(req, model, name, img.Width, img.Height, denoise: 1.0, LoraName);
        }
        else if (req.Task == AiTaskKind.Txt2Img)
        {
            int w = req.Image?.Width ?? 1024, h = req.Image?.Height ?? 1024;
            graph = WorkflowBuilder.Txt2Img(req, model, w, h, LoraName);
        }
        else throw new InvalidOperationException($"ComfyUI backend: task {req.Task} not supported yet.");

        var png = await _client.RunPromptAsync(graph, Progress, ct).ConfigureAwait(false);
        var (outRgba, ow, oh) = DecodePng(png);
        return new AiImage(outRgba, ow, oh);
    }

    /// <summary>RGB from <paramref name="rgba"/>, alpha = INVERTED mask. ComfyUI's LoadImage computes
    /// mask = 1 - alpha, so the selected (coverage 255) region must be alpha 0 → ComfyUI mask 1 → inpainted.</summary>
    private static byte[] CombineAlpha(byte[] rgba, byte[] mask, int w, int h)
    {
        var outp = (byte[])rgba.Clone();
        int n = w * h;
        for (int i = 0; i < n && i < mask.Length; i++) outp[i * 4 + 3] = (byte)(255 - mask[i]);
        return outp;
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
