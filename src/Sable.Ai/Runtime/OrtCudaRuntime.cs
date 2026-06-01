using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace Sable.Ai.Runtime;

/// <summary>
/// Activates a Sable-built, Blackwell-capable (sm_120) ONNX Runtime CUDA build on Linux.
///
/// Prebuilt ONNX Runtime ships no kernels for newer NVIDIA archs (e.g. RTX 5090 / sm_120), so the
/// CUDA EP from the NuGet package fails on those cards. Sable builds a matching ORT from source
/// (<c>tools/build-ort-cuda.sh</c>, arch-targeted) and publishes the native libs; the app downloads
/// the right one for the detected GPU (<see cref="OrtRuntimeProvisioner"/>) into <see cref="RuntimeDir"/>.
///
/// This type makes the managed ONNX Runtime load THOSE libs instead of the package's CPU native:
/// it registers a <see cref="NativeLibrary.SetDllImportResolver"/> on the ORT assembly that resolves
/// the <c>onnxruntime</c> P/Invoke to our <c>libonnxruntime.so</c>. ORT then loads its provider libs
/// (<c>libonnxruntime_providers_cuda.so</c>, …) from the same directory. Must run BEFORE the first
/// ORT call (e.g. <c>OrtEnv.Instance()</c> / any session). Windows/macOS are unaffected.
/// </summary>
public static class OrtCudaRuntime
{
    private const string MainLib = "libonnxruntime.so";
    private static readonly object _lock = new();
    private static bool _activated;

    /// <summary>App-local directory holding the downloaded sm_120 ORT native libs.</summary>
    public static string RuntimeDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sable", "ort-cuda");

    /// <summary>Path to the main ORT shared library inside <see cref="RuntimeDir"/>.</summary>
    public static string MainLibPath => Path.Combine(RuntimeDir, MainLib);

    /// <summary>True if the CUDA provider lib has been downloaded/installed locally.</summary>
    public static bool IsInstalled =>
        File.Exists(MainLibPath) && File.Exists(Path.Combine(RuntimeDir, "libonnxruntime_providers_cuda.so"));

    /// <summary>True once <see cref="Activate"/> has wired the resolver this process.</summary>
    public static bool IsActivated { get { lock (_lock) return _activated; } }

    /// <summary>
    /// Point the managed ONNX Runtime at the locally-installed sm_120 build. Idempotent; no-op off
    /// Linux or when nothing is installed. Returns true if our native is now the one ORT will load.
    /// Call before any ORT use.
    /// </summary>
    public static bool Activate()
    {
        if (!OperatingSystem.IsLinux()) return false;
        lock (_lock)
        {
            if (_activated) return true;
            if (!IsInstalled) return false;

            // Eagerly load by absolute path so the soname is resident; the resolver below also
            // returns this handle for the ORT assembly's "onnxruntime" P/Invoke.
            var handle = NativeLibrary.Load(MainLibPath);

            NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, asm, search) =>
                name is "onnxruntime" or "libonnxruntime" or "libonnxruntime.so"
                    ? handle
                    : IntPtr.Zero);

            _activated = true;
            return true;
        }
    }

    /// <summary>The native libs a complete CUDA ORT runtime needs (main + provider shims).</summary>
    public static readonly string[] RequiredLibs =
    {
        "libonnxruntime.so",
        "libonnxruntime_providers_shared.so",
        "libonnxruntime_providers_cuda.so",
    };

    /// <summary>
    /// Install a built/extracted runtime (the three <c>libonnxruntime*.so</c> files) into
    /// <see cref="RuntimeDir"/>. Accepts the libs by any versioned name (e.g.
    /// <c>libonnxruntime.so.1.24.4</c>) and normalises the main lib to <c>libonnxruntime.so</c>.
    /// </summary>
    public static void InstallFromDirectory(string srcDir)
    {
        Directory.CreateDirectory(RuntimeDir);
        foreach (var f in Directory.EnumerateFiles(srcDir))
        {
            var name = Path.GetFileName(f);
            if (!name.Contains("libonnxruntime")) continue;
            // normalise the versioned main lib (libonnxruntime.so.1.24.4) to the plain soname
            string dstName = name.StartsWith("libonnxruntime.so") && name != "libonnxruntime.so" && !name.Contains("providers")
                ? MainLib
                : name;
            File.Copy(f, Path.Combine(RuntimeDir, dstName), overwrite: true);
        }
        if (!IsInstalled)
            throw new InvalidOperationException(
                $"Installed runtime is incomplete in '{srcDir}': need {string.Join(", ", RequiredLibs)}.");
    }

    /// <summary>Remove the installed runtime (e.g. on AI disable / re-provision).</summary>
    public static void Remove()
    {
        lock (_lock)
        {
            try { if (Directory.Exists(RuntimeDir)) Directory.Delete(RuntimeDir, recursive: true); } catch { /* best effort */ }
            // resolver cannot be unregistered; _activated stays so we don't re-add, but IsInstalled is now false
        }
    }
}
