using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Sable.Ai.Sidecar.Ipc;
using Sable.Ai.Sidecar.Provisioning;
using Sable.Core.Ai;

namespace Sable.Ai.Sidecar;

/// <summary>
/// Lifecycle owner for the generative Diffusers sidecar (PHASE8_AI_SIDECAR §3.4): starts the Python
/// <c>server/</c> under a resolved <see cref="PythonEnv"/>, health-checks it, and exposes the editor-facing
/// <see cref="IAiBackend"/> (VRAM probe + availability). The app NEVER imports model code — only this
/// process boundary + the JSON/HTTP protocol. <see cref="IGenerativeModel"/> generation lands with S4; S2
/// proves the boundary with <c>health</c>/<c>vram</c>.
/// </summary>
public sealed class SidecarBackend : IAiBackend, IGenerativeBackend, IDisposable
{
    private Process? _proc;
    private SidecarClient? _client;
    private volatile bool _healthy;

    public string Name => "Diffusers sidecar";
    public AiTier Tier => AiTier.Generative;
    public bool IsAvailable => _healthy;

    /// <summary>Where the shipped Python server lives (copied beside the app).</summary>
    public static string DefaultServerDir => Path.Combine(AppContext.BaseDirectory, "server");

    /// <summary>
    /// Launch the server under <paramref name="env"/> and wait until it reports healthy. <paramref name="serverDir"/>
    /// defaults to the shipped <c>server/</c>. Throws if the process won't start; returns false if it starts
    /// but never becomes healthy within <paramref name="startupTimeout"/>.
    /// </summary>
    public async Task<bool> StartAsync(PythonEnv env, string? serverDir = null, TimeSpan? startupTimeout = null, CancellationToken ct = default)
    {
        Stop();
        serverDir ??= DefaultServerDir;
        var main = Path.Combine(serverDir, "main.py");
        if (!File.Exists(main)) throw new FileNotFoundException("sidecar server not found", main);

        int port = FreeTcpPort();
        var token = NewToken();

        var psi = new ProcessStartInfo
        {
            FileName = env.PythonExe,
            WorkingDirectory = serverDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(main);
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--token"); psi.ArgumentList.Add(token);

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!_proc.Start()) throw new InvalidOperationException("failed to start the sidecar process");

        _client = new SidecarClient(new Uri($"http://127.0.0.1:{port}/"), token);
        _healthy = await _client.WaitHealthyAsync(startupTimeout ?? TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return _healthy;
    }

    public async Task<ulong> ProbeFreeVramAsync(CancellationToken ct = default)
    {
        if (_client is null || !_healthy) return 0;
        try { var v = await _client.VramAsync(ct).ConfigureAwait(false); return v.FreeBytes < 0 ? 0 : (ulong)v.FreeBytes; }
        catch { return 0; }
    }

    /// <summary>Load a base (+ optional LoRA stack) into the running sidecar (§3.5). The request is built by
    /// <see cref="Sable.Core.Ai.LoadPlan"/> from the registry; this just forwards it over IPC.</summary>
    public Task<LoadModelResult> LoadModelAsync(LoadModelRequest req, CancellationToken ct = default)
        => _client is null || !_healthy
            ? Task.FromResult(new LoadModelResult(false, Error: "sidecar not running"))
            : _client.LoadModelAsync(req, ct);

    /// <summary>Generate from a loaded model (§4): routes by <see cref="GenRequest.Task"/> to the inpaint /
    /// outpaint / txt2img endpoint. Throws if the sidecar isn't running or the server reports an error.</summary>
    public async Task<AiImage> GenerateAsync(GenRequest req, CancellationToken ct = default)
    {
        if (_client is null || !_healthy) throw new InvalidOperationException("sidecar not running");
        var endpoint = req.Task switch
        {
            AiTaskKind.Inpaint => "inpaint",
            AiTaskKind.Outpaint => "outpaint",
            AiTaskKind.Txt2Img => "txt2img",
            _ => throw new ArgumentException($"not a generative task: {req.Task}"),
        };
        var res = await _client.GenerateAsync(endpoint, req, ct).ConfigureAwait(false);
        if (!res.Ok) throw new InvalidOperationException(string.IsNullOrEmpty(res.Error) ? "generation failed" : res.Error);
        return new AiImage(res.Rgba, res.Width, res.Height);
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

    private static string NewToken()
    {
        Span<byte> b = stackalloc byte[24];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b);
    }
}
