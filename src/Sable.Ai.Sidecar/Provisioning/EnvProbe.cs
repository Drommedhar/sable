using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sable.Ai.Sidecar.Provisioning;

/// <summary>
/// Real <see cref="IEnvProbe"/>: runs a tiny inline script through the candidate python and parses its JSON
/// (PHASE8_AI_SIDECAR §3.2). Inline (<c>python -c</c>) so there's no script file to locate. Times out so a
/// hung/foreign interpreter can't block resolution. Any failure → null (treated as "not usable").
/// </summary>
public sealed class EnvProbe : IEnvProbe
{
    private readonly int _timeoutMs;
    public EnvProbe(int timeoutMs = 15_000) => _timeoutMs = timeoutMs;

    // single-line-friendly; prints one JSON object. Missing torch/diffusers → empty strings (still valid JSON).
    private const string Script =
        "import json,platform\n" +
        "d={'os':platform.system(),'python':platform.python_version(),'torch':'','diffusers':'','cuda':'','cuda_avail':False,'mps':False,'rocm':False,'directml':False}\n" +
        "try:\n" +
        " import torch\n" +
        " d['torch']=torch.__version__\n" +
        " d['cuda']=getattr(torch.version,'cuda',None) or ''\n" +
        " d['cuda_avail']=bool(torch.cuda.is_available())\n" +
        " d['rocm']=bool(getattr(torch.version,'hip',None))\n" +
        " mps=getattr(torch.backends,'mps',None)\n" +
        " d['mps']=bool(mps and mps.is_available())\n" +
        "except Exception: pass\n" +
        "try:\n" +
        " import torch_directml\n" +
        " d['directml']=True\n" +
        "except Exception: pass\n" +
        "try:\n" +
        " import diffusers\n" +
        " d['diffusers']=diffusers.__version__\n" +
        "except Exception: pass\n" +
        "print(json.dumps(d))\n";

    public async Task<EnvCaps?> ProbeAsync(string pythonExe, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(Script);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeoutMs);
            try { await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return null; }

            var json = (await stdoutTask.ConfigureAwait(false)).Trim();
            return Parse(json);
        }
        catch { return null; }
    }

    /// <summary>Parse the probe JSON line into <see cref="EnvCaps"/>; null on malformed input. Pure → testable.</summary>
    public static EnvCaps? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        // tolerate extra lines (warnings) before the JSON: take the last '{'..'}' span
        int s = json.LastIndexOf('{'), e = json.LastIndexOf('}');
        if (s < 0 || e < s) return null;
        try
        {
            using var doc = JsonDocument.Parse(json.Substring(s, e - s + 1));
            var r = doc.RootElement;
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool Bool(string k) => r.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True);
            var cuda = Str("cuda");
            return new EnvCaps(
                TorchVersion: Str("torch"),
                DiffusersVersion: Str("diffusers"),
                CudaVersion: string.IsNullOrEmpty(cuda) ? null : cuda,
                Cuda: Bool("cuda_avail"),
                Mps: Bool("mps"),
                Rocm: Bool("rocm"),
                DirectMl: Bool("directml"));
        }
        catch { return null; }
    }
}
