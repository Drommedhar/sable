using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Sable.Ai.Sidecar.Provisioning;

/// <summary>
/// Picks the right PyTorch CUDA wheel tag (<c>cu118</c>…<c>cu128</c>) for the host GPU instead of hardcoding
/// one (PHASE8_AI_COMFY). Blackwell (RTX 50xx, compute 12.0) has NO kernels in cu124 ("no kernel image") and
/// needs cu128; older archs use the newest wheel the DRIVER's CUDA version allows. <see cref="Tag"/> is pure
/// (unit-tested); <see cref="Detect"/> queries <c>nvidia-smi</c> and falls back to a safe modern default.
/// </summary>
public static class CudaWheel
{
    /// <summary>Choose a cu-tag from the GPU compute capability + the driver's max CUDA version. Pure.</summary>
    public static string Tag(double computeCap, double driverCuda)
    {
        if (computeCap >= 12.0) return "cu128";          // Blackwell — only cu128+ has sm_120 kernels
        if (driverCuda >= 12.8) return "cu128";
        if (driverCuda >= 12.6) return "cu126";
        if (driverCuda >= 12.4) return "cu124";
        if (driverCuda >= 12.1) return "cu121";
        return "cu118";                                  // oldest supported CUDA wheel
    }

    /// <summary>Detect the cu-tag via <c>nvidia-smi</c> (compute cap + driver CUDA); "cu128" if undetectable.</summary>
    public static string Detect()
    {
        try
        {
            double cap = FirstDouble(RunSmi("--query-gpu=compute_cap --format=csv,noheader"));
            double driver = DriverCuda();
            if (cap <= 0 && driver <= 0) return "cu128";
            return Tag(cap, driver <= 0 ? 99 : driver);
        }
        catch { return "cu128"; }
    }

    private static double DriverCuda()
    {
        var m = Regex.Match(RunSmi(""), @"CUDA Version:\s*([0-9]+\.[0-9]+)");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static double FirstDouble(string s)
    {
        var m = Regex.Match(s, @"([0-9]+\.[0-9]+)");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string RunSmi(string args)
    {
        var psi = new ProcessStartInfo("nvidia-smi", args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p is null) return "";
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(8000);
        return outp;
    }
}
