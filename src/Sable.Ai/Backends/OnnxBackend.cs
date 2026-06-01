using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Sable.Ai.Adapters;
using Sable.Core.Ai;

namespace Sable.Ai.Backends;

/// <summary>
/// Light-tier backend (PHASE8_AI §1.2/§1.5): hosts ONNX Runtime <see cref="InferenceSession"/>s on a
/// per-OS GPU execution provider — DirectML on Windows, CUDA on Linux, WebGPU (Metal via Dawn) on
/// macOS. Sessions are cached per model path. Per the GPU-only policy there is NO blanket CPU EP
/// fallback — if the platform GPU EP isn't present the backend reports unavailable and refuses to
/// create a session.
/// </summary>
public sealed class OnnxBackend : IAiBackend, IDisposable
{
    private readonly Dictionary<string, InferenceSession> _sessions = new();
    private readonly Dictionary<string, InferenceSession> _cpuSessions = new();
    private readonly object _lock = new();

    public string Name { get; }
    public AiTier Tier => AiTier.Light;
    public bool IsAvailable { get; }

    // GPU EP is chosen at runtime per OS: Linux=CUDA (from Sable's sm_120 ORT build), Windows=DirectML,
    // macOS=WebGPU (Metal via Dawn, in the base package's osx-arm64 native). The managed API exposes
    // every Append* method regardless of which native is shipped, so no #if/DefineConstants are needed.
    // (macOS uses WebGPU, not CoreML: on these models CoreML's MLProgram path fails on dynamic dims,
    // compiles SAM2 in minutes, and rejects ESRGAN — WebGPU runs them all, faster, with no flag fiddling.)
    private readonly bool _useCuda;
    private readonly bool _useWebGpu;

    public OnnxBackend()
    {
        if (OperatingSystem.IsLinux())
        {
            // Make ORT load Sable's downloaded sm_120 build BEFORE any other ORT call, then confirm
            // the CUDA provider is actually present. No build downloaded yet → unavailable (NoGpu).
            bool active = Runtime.OrtCudaRuntime.Activate();
            bool cuda = false;
            if (active)
                try { cuda = OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider"); }
                catch { /* native failed to load */ }
            _useCuda = active && cuda;
            IsAvailable = _useCuda;
            Name = "ONNX (CUDA)";
        }
        else if (OperatingSystem.IsMacOS())
        {
            // WebGPU EP ships in the base package's osx-arm64 native (no download, unlike Linux/CUDA).
            bool webgpu = false;
            try { webgpu = OrtEnv.Instance().GetAvailableProviders().Contains("WebGpuExecutionProvider"); }
            catch { /* ORT failed to load → unavailable */ }
            _useWebGpu = webgpu;
            IsAvailable = webgpu;
            Name = "ONNX (WebGPU)";
        }
        else
        {
            bool dml = false;
            try { dml = OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider"); }
            catch { /* ORT failed to load → unavailable */ }
            IsAvailable = dml;
            Name = "ONNX (DirectML)";
        }
    }

    public Task<ulong> ProbeFreeVramAsync(CancellationToken ct = default) => Task.FromResult(0UL);

    /// <summary>Get (or create + cache) a session for a model file. <paramref name="cpu"/>=true forces the
    /// CPU EP (used as a fallback when the GPU can't run a model, e.g. SAM2 on a weak laptop GPU that TDRs).</summary>
    public InferenceSession GetSession(string modelPath, bool cpu) => cpu ? GetCpuSession(modelPath) : GetSession(modelPath);

    /// <summary>Get (or create + cache) a DirectML session for a model file. Throws if DML is absent.</summary>
    public InferenceSession GetSession(string modelPath)
    {
        if (!IsAvailable)
        {
            string ep = OperatingSystem.IsLinux() ? "CUDA" : OperatingSystem.IsMacOS() ? "WebGPU" : "DirectML";
            throw new InvalidOperationException(
                $"{ep} execution provider not available (AI is GPU-only, no CPU fallback).");
        }
        if (!modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{Path.GetFileName(modelPath)}' is not an ONNX model. The light tier runs ONNX Runtime — " +
                "a .pth / .pt / .safetensors / .ckpt must be exported to .onnx first (or download an .onnx build).");
        lock (_lock)
        {
            if (_sessions.TryGetValue(modelPath, out var existing)) return existing;
            var opts = new SessionOptions();
            if (_useCuda)
            {
                opts.AppendExecutionProvider_CUDA(0);
            }
            else if (_useWebGpu)
            {
                // WebGPU EP (Metal via Dawn). General compute EP — handles dynamic shapes like CUDA/DML
                // (unlike CoreML's graph compiler), so no per-model flag fiddling. Appended via the
                // string API (no typed AppendExecutionProvider_WebGPU helper in the managed assembly).
                opts.AppendExecutionProvider("WebGPU", new Dictionary<string, string>());
            }
            else
            {
                // DirectML requirements: sequential exec + no memory pattern.
                opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                opts.EnableMemoryPattern = false;
                opts.AppendExecutionProvider_DML(0);
            }
            var sess = new InferenceSession(modelPath, opts);
            _sessions[modelPath] = sess;
            return sess;
        }
    }

    /// <summary>
    /// Get (or create + cache) a CPU session for a model. Narrow exception to the GPU-only rule: a few
    /// models (LaMa's Fast-Fourier convolutions) can't run on DirectML, so they run on the CPU provider.
    /// Used only by adapters that opt in; the heavy models stay on the GPU.
    /// </summary>
    public InferenceSession GetCpuSession(string modelPath)
    {
        if (!modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{Path.GetFileName(modelPath)}' is not an ONNX model.");
        lock (_lock)
        {
            if (_cpuSessions.TryGetValue(modelPath, out var existing)) return existing;
            var sess = new InferenceSession(modelPath, new SessionOptions());   // default EP = CPU
            _cpuSessions[modelPath] = sess;
            return sess;
        }
    }

    /// <summary>Build the segmentation/matting adapter for a model manifest (BiRefNet/RMBG/SAM2 later).</summary>
    public IMaskModel CreateMaskModel(ModelManifest m)
    {
        var path = m.Files?.FirstOrDefault()
            ?? throw new InvalidOperationException($"Model '{m.Id}' has no weights file.");
        if (m.Adapter == "sam2")
        {
            if (m.Files is not { Count: >= 2 })
                throw new InvalidOperationException($"SAM2 model '{m.Id}' needs two files: Files[0]=encoder, Files[1]=decoder.");
            return new Sam2Adapter(this, m.Files[0], m.Files[1], m.InputSize);
        }
        return m.Adapter switch
        {
            "matte" => new BiRefNetAdapter(this, path, m.InputSize),
            _ => new BiRefNetAdapter(this, path, m.InputSize),   // default matte path for 8.1
        };
    }

    /// <summary>Build the image→image adapter for a model manifest (ESRGAN upscale / later LaMa, denoise).</summary>
    public IRasterModel CreateRasterModel(ModelManifest m)
    {
        var path = m.Files?.FirstOrDefault()
            ?? throw new InvalidOperationException($"Model '{m.Id}' has no weights file.");
        return m.Adapter switch
        {
            "esrgan" => new EsrganAdapter(this, path),
            "lama" => new LamaAdapter(this, path),
            _ => new EsrganAdapter(this, path),   // default upscale path for 8.2
        };
    }

    /// <summary>Dispose + drop the cached GPU (DML/CUDA) sessions. After a device-lost (TDR) the D3D/DML
    /// device is removed process-wide and cached sessions are poisoned, so we clear them before any retry.</summary>
    public void ResetGpuSessions()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values) { try { s.Dispose(); } catch { /* device already gone */ } }
            _sessions.Clear();
        }
    }

    /// <summary>True if the exception is a GPU device-lost / hung (DXGI_ERROR_DEVICE_HUNG 0x887A0006,
    /// REMOVED 0x887A0005, RESET 0x887A0007) surfaced by ORT's DML provider — the signal to fall back
    /// to CPU. Matches the HRESULT codes (locale-independent); leans broad since it only gates a retry.</summary>
    public static bool IsDeviceLost(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is not OnnxRuntimeException) continue;
            var m = e.Message ?? "";
            if (m.Contains("887A0006") || m.Contains("887A0005") || m.Contains("887A0007") ||
                m.Contains("DXGI_ERROR_DEVICE", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("dml_provider_factory") ||
                m.Contains("device removed", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("device hung", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
            foreach (var s in _cpuSessions.Values) s.Dispose();
            _cpuSessions.Clear();
        }
    }
}
